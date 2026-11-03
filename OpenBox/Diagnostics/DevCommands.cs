#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenBox.Apps;
using OpenBox.Packages;
using OpenBox.Store;
using Windows.Storage;
using Windows.UI.Xaml;

namespace OpenBox.Diagnostics
{
    /// <summary>
    /// Debug builds only: lets openbox be driven without a controller, since Device Portal has no
    /// way to press buttons. A line saved as LocalState\dev-command.txt (Device Portal's file
    /// explorer can upload it) is run, then the file deleted; what happens goes to the log.
    ///
    ///   open &lt;id&gt;      open a homescreen app, as A on its tile
    ///   install &lt;id&gt;   install a store app, as A on it in the Store
    ///   uninstall &lt;id&gt; uninstall a homescreen app, as Uninstall in its menu (asks first)
    ///   home           go back to the homescreen
    /// </summary>
    internal static class DevCommands
    {
        private static DispatcherTimer _timer;

        public static void Start()
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "dev-command.txt");
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) =>
            {
                string command;
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }
                    command = File.ReadAllText(path).Trim();
                    File.Delete(path);
                }
                catch (IOException)
                {
                    return; // Still being written; next tick.
                }
                Run(command);
            };
            _timer.Start();
        }

        private static async void Run(string command)
        {
            Log.Write("Dev command: " + command);
            var parts = command.Split(new[] { ' ' }, 2);
            var id = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            try
            {
                switch (parts[0])
                {
                    case "open":
                        App.Open((await AppLibrary.GetHomeAppsAsync()).First(app => app.Id == id));
                        break;
                    case "install":
                        var storeApp = (await StoreIndex.LoadAsync()).First(app => app.Id == id);
                        if (storeApp.IsPackage)
                        {
                            var installation = PackageApps.Install(storeApp);
                            var logged = string.Empty;
                            installation.Changed += (s, e) =>
                            {
                                // Each step, and every 10% of it.
                                var match = Regex.Match(installation.Status, @"^(\D*?)(\d+)%");
                                var step = match.Success ? match.Groups[1].Value + int.Parse(match.Groups[2].Value) / 10 : installation.Status;
                                if (step != logged)
                                {
                                    logged = step;
                                    Log.Write(id + ": " + installation.Status);
                                }
                            };
                        }
                        else
                        {
                            await AppLibrary.InstallAsync(storeApp);
                        }
                        break;
                    case "uninstall":
                        await AppActions.UninstallAsync((await AppLibrary.GetHomeAppsAsync()).First(app => app.Id == id), true);
                        break;
                    case "home":
                        App.GoHome();
                        break;
                    default:
                        Log.Write("Unknown dev command");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Dev command failed", ex);
            }
        }
    }
}
#endif
