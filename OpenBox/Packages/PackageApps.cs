using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using OpenBox.Apps;
using OpenBox.Diagnostics;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.System;
using Windows.Web.Http;

namespace OpenBox.Packages
{
    /// <summary>
    /// Installs, launches and uninstalls package apps on the console itself, with the system's
    /// PackageManager (the packageManagement and packageQuery capabilities in the manifest). The
    /// package is downloaded from where its developer publishes it, never from openbox, and
    /// checked against the SHA-256 they publish with it.
    /// </summary>
    internal static class PackageApps
    {
        private static readonly Dictionary<string, Installation> Installations = new Dictionary<string, Installation>();

        /// <summary>The app's installation in progress (or just failed), if any.</summary>
        public static Installation GetInstallation(AppEntry app)
        {
            Installation installation;
            return Installations.TryGetValue(app.Id, out installation) ? installation : null;
        }

        /// <summary>Starts installing the app, or returns the installation already under way.</summary>
        public static Installation Install(AppEntry app)
        {
            var installation = GetInstallation(app);
            if (installation == null || installation.Failed)
            {
                installation = new Installation(app);
                Installations[app.Id] = installation;
                installation.Run();
            }
            return installation;
        }

        /// <summary>Whether the app's package is installed on the console (by openbox or anything else).</summary>
        public static bool IsOnConsole(AppEntry app)
        {
            return Find(app) != null;
        }

        /// <summary>Opens the app's package. Null if it opened, otherwise why not.</summary>
        public static async Task<string> LaunchAsync(AppEntry app)
        {
            var package = Find(app);
            if (package == null)
            {
                Log.Write(app.Id + " isn't installed");
                return "It isn't installed on this console any more. Uninstall it from the homescreen and install it again from the Store.";
            }
            var entries = await package.GetAppListEntriesAsync();
            Log.Write(package.Id.FullName + " has " + entries.Count + " app(s): " + string.Join(", ", entries.Select(e => e.AppUserModelId)));
            var entry = entries.FirstOrDefault(e => e.AppUserModelId.EndsWith("!" + app.Package.AppId, StringComparison.Ordinal)) ?? entries.FirstOrDefault();
            if (entry == null)
            {
                return "Its package has no app to open.";
            }
            if (await entry.LaunchAsync())
            {
                Log.Write("Launched " + entry.AppUserModelId);
                return null;
            }
            // The console has users of its own; the launch may need to say whose it is.
            foreach (var user in await User.FindAllAsync())
            {
                try
                {
                    if (await entry.LaunchForUserAsync(user))
                    {
                        Log.Write("Launched " + entry.AppUserModelId + " for a " + user.Type + " user");
                        return null;
                    }
                    Log.Write("Launch for a " + user.Type + " user refused");
                }
                catch (Exception ex)
                {
                    Log.Error("Launch for a " + user.Type + " user failed", ex);
                }
            }
            Log.Write("Couldn't launch " + entry.AppUserModelId);
            return "The console wouldn't open it.";
        }

        /// <summary>Removes the app's package from the console, with its data.</summary>
        public static async Task UninstallAsync(AppEntry app)
        {
            var package = Find(app);
            if (package == null)
            {
                return;
            }
            Log.Write("Uninstalling " + package.Id.FullName);
            var result = await new PackageManager().RemovePackageAsync(package.Id.FullName);
            if (result.ExtendedErrorCode != null)
            {
                throw new Exception(result.ErrorText, result.ExtendedErrorCode);
            }
        }

        private static Package Find(AppEntry app)
        {
            try
            {
                return new PackageManager().FindPackagesForUser(string.Empty, app.Package.FamilyName).FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Error("Looking for " + app.Package.FamilyName + " failed", ex);
                return null;
            }
        }
    }

    /// <summary>One package app being installed: download, check, install, then its tile.</summary>
    internal sealed class Installation
    {
        private readonly AppEntry _app;

        public Installation(AppEntry app)
        {
            _app = app;
        }

        /// <summary>Raised on the UI thread whenever <see cref="Status"/> changes.</summary>
        public event EventHandler Changed;

        public string Status { get; private set; }
        public bool Done { get; private set; }
        public bool Failed { get; private set; }

        public async void Run()
        {
            StorageFile file = null;
            try
            {
                if (PackageApps.IsOnConsole(_app))
                {
                    // Installed some other way (Device Portal, say): it only needs its tile.
                    Log.Write(_app.Id + " is already on the console");
                }
                else
                {
                    file = await DownloadAsync();
                    if (_app.Package.Sha256Url != null)
                    {
                        await CheckAsync(file);
                    }
                    await DeployAsync(file);
                }
                await AppLibrary.InstallAsync(_app);
                Done = true;
                Set("✓ On your homescreen");
            }
            catch (Exception ex)
            {
                Log.Error("Installing " + _app.Id + " failed", ex);
                Failed = true;
                Set("Couldn't install (" + Describe(ex) + "). A to try again.");
            }
            finally
            {
                if (file != null)
                {
                    // It's large, and installed now (or useless).
                    try
                    {
                        await file.DeleteAsync();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private async Task<StorageFile> DownloadAsync()
        {
            Set("Downloading...");
            var file = await ApplicationData.Current.LocalCacheFolder.CreateFileAsync(_app.Id + ".appx", CreationCollisionOption.ReplaceExisting);
            Log.Write("Downloading " + _app.Package.Url);
            var download = new BackgroundDownloader().CreateDownload(_app.Package.Url, file);
            var progress = new Progress<DownloadOperation>(operation =>
            {
                var total = operation.Progress.TotalBytesToReceive;
                var received = operation.Progress.BytesReceived;
                if (total > 0)
                {
                    Set(string.Format("Downloading {0:0}%  ({1:0} of {2:0} MB)", 100.0 * received / total, received / 1048576.0, total / 1048576.0));
                }
            });
            await download.StartAsync().AsTask(progress);
            var status = download.GetResponseInformation()?.StatusCode ?? 0;
            if (status != 200)
            {
                throw new Exception("the download answered " + status);
            }
            Log.Write(string.Format("Downloaded {0:0} MB", download.Progress.BytesReceived / 1048576.0));
            return file;
        }

        private async Task CheckAsync(StorageFile file)
        {
            Set("Checking the download...");
            string expected;
            using (var client = new HttpClient())
            {
                var text = await client.GetStringAsync(_app.Package.Sha256Url);
                expected = text.Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
            }
            var actual = await Task.Run(() =>
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(file.Path))
                {
                    return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
                }
            });
            if (actual != expected)
            {
                throw new Exception("the download doesn't match its published SHA-256");
            }
            Log.Write("SHA-256 matches: " + actual);
        }

        private async Task DeployAsync(StorageFile file)
        {
            Set("Installing...");
            Log.Write("Installing " + file.Name);
            var progress = new Progress<DeploymentProgress>(p => Set(string.Format("Installing {0}%", p.percentage)));
            var result = await new PackageManager()
                .AddPackageAsync(new Uri(file.Path), null, DeploymentOptions.ForceApplicationShutdown)
                .AsTask(progress);
            if (result.ExtendedErrorCode != null)
            {
                throw new Exception(result.ErrorText, result.ExtendedErrorCode);
            }
            Log.Write("Installed " + _app.Package.FamilyName);
        }

        private void Set(string status)
        {
            Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // The message's first line, and the error code if the system gave one.
        private static string Describe(Exception ex)
        {
            var code = (ex.InnerException ?? ex).HResult;
            var message = (ex.Message ?? string.Empty).Split('\r', '\n')[0].TrimEnd('.');
            return message + (code != 0 && code != -2146233088 ? string.Format(", 0x{0:X8}", code) : string.Empty);
        }
    }
}
