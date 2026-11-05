using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using OpenBox.Diagnostics;
using Windows.Storage;
using Windows.Web.Http;

namespace OpenBox.Spelunky
{
    /// <summary>
    /// The game's files: its sprites, sounds and rooms. OpenBox doesn't carry them. The first time
    /// the game opens, they're downloaded from the port's own release on GitHub (the Windows zip, the
    /// release spelunky.dll is built from), checked against the release's SHA-256, and its assets/
    /// folder, LICENSE and NOTICE.txt kept in LocalState\Spelunky.
    /// </summary>
    internal static class SpelunkyAssets
    {
        /// <summary>The release: the tag in Spelunky/Cargo.toml, which spelunky.dll is built from.</summary>
        public const string Version = "0.1.19";
        private const string Zip = "spelunky-" + Version + "-windows-x64.zip";
        public const string Url = "https://github.com/851-labs/spelunky/releases/download/v" + Version + "/" + Zip;
        /// <summary>The release's SHA256SUMS line for <see cref="Zip"/>.</summary>
        private const string Sha256 = "d6919756de0203694644eeff9fa1f8d0c1f278de7938c4dffd0e79b3f384d580";
        /// <summary>Written last, so a download that stopped partway is started again.</summary>
        private const string DoneFile = "complete";

        /// <summary>LocalState\Spelunky: the log, the saves (save\) and each release's files.</summary>
        public static string DataFolder
        {
            get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "Spelunky"); }
        }

        private static string ReleaseFolder
        {
            get { return Path.Combine(DataFolder, "release-" + Version); }
        }

        /// <summary>The assets folder spelunky.dll reads.</summary>
        public static string AssetsFolder
        {
            get { return Path.Combine(ReleaseFolder, "assets"); }
        }

        /// <summary>The license and notice the release comes with, which must go with every copy.</summary>
        public static string NoticeFile
        {
            get { return Path.Combine(ReleaseFolder, "NOTICE.txt"); }
        }

        public static bool Ready
        {
            get { return File.Exists(Path.Combine(ReleaseFolder, DoneFile)); }
        }

        /// <summary>Hears the download's progress (0 to 1), on the UI thread: the page showing it.</summary>
        public static Action<double> Progress;

        private static Task _downloading;

        /// <summary>
        /// Downloads and unpacks the release, unless it's here already. One download at a time: a
        /// second call while one runs (the page left and opened again) waits for the same one. On the
        /// UI thread.
        /// </summary>
        public static Task DownloadAsync()
        {
            if (Ready)
            {
                return Task.CompletedTask;
            }
            if (_downloading == null || _downloading.IsCompleted)
            {
                _downloading = DownloadOnceAsync();
            }
            return _downloading;
        }

        private static async Task DownloadOnceAsync()
        {
            Directory.CreateDirectory(DataFolder);
            var zipPath = Path.Combine(DataFolder, Zip);
            Log.Write("Spelunky: downloading " + Url);
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(new Uri(Url), HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = (double)(response.Content.Headers.ContentLength ?? 18000000);
                using (var input = (await response.Content.ReadAsInputStreamAsync()).AsStreamForRead())
                using (var output = File.Create(zipPath))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        done += read;
                        Progress?.Invoke(Math.Min(0.95, 0.95 * done / total));
                    }
                }
            }
            await Task.Run(() => Unpack(zipPath));
            Progress?.Invoke(1);
            Log.Write("Spelunky: files ready in " + ReleaseFolder);
        }

        private static void Unpack(string zipPath)
        {
            try
            {
                string hash;
                using (var file = File.OpenRead(zipPath))
                using (var sha = SHA256.Create())
                {
                    hash = string.Concat(sha.ComputeHash(file).Select(b => b.ToString("x2")));
                }
                if (hash != Sha256)
                {
                    throw new InvalidDataException("The download isn't the release's (its SHA-256 is " + hash + ")");
                }
                if (Directory.Exists(ReleaseFolder))
                {
                    Directory.Delete(ReleaseFolder, true);
                }
                Directory.CreateDirectory(ReleaseFolder);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        // spelunky-<version>-windows-x64/<path>: the assets and the license, not the game's .exe.
                        var slash = entry.FullName.IndexOf('/');
                        var path = slash < 0 ? string.Empty : entry.FullName.Substring(slash + 1);
                        var wanted = path.StartsWith("assets/") || path == "LICENSE" || path == "NOTICE.txt";
                        if (!wanted || path.EndsWith("/") || path.Contains(".."))
                        {
                            continue;
                        }
                        var target = Path.Combine(ReleaseFolder, path.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }
                File.WriteAllText(Path.Combine(ReleaseFolder, DoneFile), Version);
            }
            finally
            {
                File.Delete(zipPath);
            }
        }
    }
}
