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
using Windows.Gaming.Input;
using Windows.Management.Deployment;
using Windows.Security.Credentials;
using Windows.Security.Cryptography.Certificates;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.UI.Input.Preview.Injection;
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
    ///   remove-package &lt;familyName&gt;  uninstall a package openbox installed, tile or not
    ///   probe &lt;url&gt; [user] [password]  GET an address, ignoring certificate errors (the
    ///                  console's own Device Portal, say); the credentials aren't logged
    ///   press &lt;action&gt;... controller presses in a web app, 0.8 s apart: up, down, left, right,
    ///                  select (A), back (B), x, y, lb, rb, lt, rt, menu
    ///   script &lt;js&gt;    run JavaScript in a web app's page and log what it returns
    ///   hidekeyboard   close the console's keyboard, as its B button would
    ///   pad &lt;button&gt;...  a real controller press, injected into the console's input (so the
    ///                  console's keyboard gets it too): a, b, x, y, up, down, left, right, menu,
    ///                  view, lb, rb, lt, rt, each optionally *n (right*4); 0.35 s apart. Needs
    ///                  the inputInjectionBrokered capability, which the workflow adds to Debug
    ///                  builds only
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

        private static InputInjector _injector;

        private static async Task PadAsync(string[] presses)
        {
            if (_injector == null)
            {
                _injector = InputInjector.TryCreate();
                if (_injector == null)
                {
                    Log.Write("pad: no input injector");
                    return;
                }
                _injector.InitializeGamepadInjection();
                await Task.Delay(500);
            }
            foreach (var press in presses)
            {
                var parts = press.Split('*');
                var times = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                var info = new InjectedInputGamepadInfo();
                switch (parts[0])
                {
                    case "a": info.Buttons = GamepadButtons.A; break;
                    case "b": info.Buttons = GamepadButtons.B; break;
                    case "x": info.Buttons = GamepadButtons.X; break;
                    case "y": info.Buttons = GamepadButtons.Y; break;
                    case "up": info.Buttons = GamepadButtons.DPadUp; break;
                    case "down": info.Buttons = GamepadButtons.DPadDown; break;
                    case "left": info.Buttons = GamepadButtons.DPadLeft; break;
                    case "right": info.Buttons = GamepadButtons.DPadRight; break;
                    case "menu": info.Buttons = GamepadButtons.Menu; break;
                    case "view": info.Buttons = GamepadButtons.View; break;
                    case "lb": info.Buttons = GamepadButtons.LeftShoulder; break;
                    case "rb": info.Buttons = GamepadButtons.RightShoulder; break;
                    case "lt": info.LeftTrigger = 1; break;
                    case "rt": info.RightTrigger = 1; break;
                    default:
                        Log.Write("pad: no button " + parts[0]);
                        return;
                }
                for (var i = 0; i < times; i++)
                {
                    _injector.InjectGamepadInput(info);
                    await Task.Delay(120);
                    _injector.InjectGamepadInput(new InjectedInputGamepadInfo());
                    await Task.Delay(350);
                }
            }
            Log.Write("pad: done");
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
                    case "remove-package":
                        var installed = new PackageManager().FindPackagesForUser(string.Empty, id).ToList();
                        foreach (var package in installed)
                        {
                            var result = await new PackageManager().RemovePackageAsync(package.Id.FullName);
                            Log.Write("Removed " + package.Id.FullName + (result.ExtendedErrorCode != null ? ": " + result.ErrorText : string.Empty));
                        }
                        if (installed.Count == 0)
                        {
                            Log.Write("No package " + id);
                        }
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
                    case "hidekeyboard":
                        var typing = App.CurrentPage as WebAppPage;
                        Log.Write(typing == null ? "No web app is open" : "Keyboard hidden: " + typing.HideKeyboard());
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
                    case "pad":
                        await PadAsync(id.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
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
