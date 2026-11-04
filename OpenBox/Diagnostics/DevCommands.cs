#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenBox.Apps;
using OpenBox.Packages;
using OpenBox.Store;
using OpenBox.Web;
using Windows.Security.Credentials;
using Windows.Security.Cryptography.Certificates;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
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
    ///   uninstall &lt;id&gt; uninstall a homescreen app and its package, without asking
    ///   home           go back to the homescreen
    ///   probe &lt;url&gt; [user] [password]  GET an address, ignoring certificate errors (the
    ///                  console's own Device Portal, say); the credentials aren't logged
    ///   press &lt;action&gt;... controller presses in a web app, 0.8 s apart: up, down, left, right,
    ///                  select (A), back (B), x, y, lb, rb, lt, rt, menu
    ///   script &lt;js&gt;    run JavaScript in a web app's page and log what it returns
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
            Log.Write("Dev command: " + (command.StartsWith("probe ") ? string.Join(" ", command.Split(' ').Take(2)) : command));
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
                        var homeApp = (await AppLibrary.GetHomeAppsAsync()).First(app => app.Id == id);
                        if (homeApp.IsPackage)
                        {
                            await PackageApps.UninstallAsync(homeApp);
                        }
                        await AppLibrary.UninstallAsync(homeApp.Id);
                        break;
                    case "home":
                        App.GoHome();
                        break;
                    case "probe":
                        var args = id.Split(' ');
                        var filter = new HttpBaseProtocolFilter();
                        filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Untrusted);
                        filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.InvalidName);
                        filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Expired);
                        if (args.Length >= 3)
                        {
                            filter.ServerCredential = new PasswordCredential("probe", args[1], args[2]);
                        }
                        using (var client = new HttpClient(filter))
                        {
                            try
                            {
                                var response = await client.GetAsync(new Uri(args[0]));
                                var body = await response.Content.ReadAsStringAsync();
                                Log.Write("probe " + args[0] + ": " + (int)response.StatusCode + " " + body.Substring(0, Math.Min(body.Length, 200)));
                            }
                            catch (Exception ex)
                            {
                                Log.Write(string.Format("probe {0}: 0x{1:X8} {2}", args[0], ex.HResult, ex.Message.Split('\r', '\n')[0]));
                            }
                        }
                        break;
                    case "script":
                        var scripted = App.CurrentPage as WebAppPage;
                        Log.Write(scripted == null ? "No web app is open" : "Script: " + await scripted.RunScriptAsync(id));
                        break;
                    case "press":
                        foreach (var action in id.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var page = App.CurrentPage as WebAppPage;
                            if (page == null)
                            {
                                Log.Write("No web app is open");
                                break;
                            }
                            page.Press(action);
                            await Task.Delay(800);
                        }
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
