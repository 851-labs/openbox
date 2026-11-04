using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using OpenBox.AdBlock;
using OpenBox.Apps;
using OpenBox.Diagnostics;
using Windows.Data.Json;
using Windows.Storage;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.Web
{
    /// <summary>
    /// A web app, full screen in a WebView2. The controller drives it one of two ways
    /// (<see cref="AppEntry.RemoteInput"/>): as a TV remote, for web apps made for TVs (YouTube),
    /// or as the console's mouse pointer, as in a browser. B goes back within the app (a TV app
    /// gets it as its own back key, and goes home when it closes itself; a web page goes back a
    /// page) and View goes home. A TV app whose script reads the controller itself (X) can also ask
    /// for the pointer for a while, for pages it can't drive (sign-in).
    /// </summary>
    public sealed partial class WebAppPage : Page, IBackHandler
    {
        private static readonly bool IsXbox = AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

        private AppEntry _app;
        private IWebAppExtension _extension;
        private AdBlocker _adBlocker;
        private CoreWebView2 _core;
        private ControllerBridge _controller;
        private bool _loaded;
        private bool _closed;
        private bool _pointer;

        public WebAppPage()
        {
            this.InitializeComponent();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        // The page reads B from the controller itself (it goes to the app), so the system's back
        // request, which B also raises, has nothing left to do.
        bool IBackHandler.HandleBack()
        {
            return true;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _app = (AppEntry)e.Parameter;
            Splash.Background = _app.Background;
            SplashIcon.Source = _app.IconImage;
            // The console's mouse pointer for ordinary web pages, none for TV apps.
            SetPointer(!_app.RemoteInput);
            StartAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _closed = true;
            _controller?.Dispose();
            _extension?.Dispose();
            _adBlocker?.Dispose();
            if (_core != null)
            {
                _core.WebMessageReceived -= OnWebMessageReceived;
            }
            // Ends the app's browser process now rather than when the page is collected.
            WebView.Close();
        }

        private async void StartAsync()
        {
            Log.Write("Opening " + _app.Id);
            try
            {
                await WebView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 failed to start", ex);
                ShowLoadError("The web view could not start. " + ex.Message);
                return;
            }
            if (_closed)
            {
                return;
            }

            _core = WebView.CoreWebView2;
            var settings = _core.Settings;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            // No browser shortcuts such as the back key: B is handled by ControllerBridge only.
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
#endif
            _core.NavigationStarting += OnNavigationStarting;
            _core.NavigationCompleted += OnNavigationCompleted;
            _core.NewWindowRequested += OnNewWindowRequested;
            _core.ProcessFailed += OnProcessFailed;
            _core.WebMessageReceived += OnWebMessageReceived;

            try
            {
                _extension = _app.CreateExtension?.Invoke();
                if (_extension != null)
                {
                    await _extension.AttachAsync(_core);
                }
                else
                {
                    // Every other web app gets the uBlock Origin style blocker; YouTube's own
                    // (TubeBox's) is tuned to its TV app. Without it the app still opens.
                    try
                    {
                        _adBlocker = await AdBlocker.AttachAsync(_core, _app.Url);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("The ad blocker failed to start", ex);
                    }
                }
                var userAgent = _extension?.UserAgent ?? _app.UserAgent;
                if (!string.IsNullOrEmpty(userAgent))
                {
                    settings.UserAgent = userAgent;
                }
                await _core.AddScriptToExecuteOnDocumentCreatedAsync(await BuildPageScriptAsync());
            }
            catch (Exception ex)
            {
                Log.Error("Preparing " + _app.Id + " failed", ex);
                ShowLoadError("openbox couldn't prepare " + _app.Name + ". " + ex.Message);
                return;
            }
            if (_closed)
            {
                return;
            }

            _controller = new ControllerBridge(OnControllerAction);
            Log.Write("Loading " + Log.DescribeUrl(_app.Url));
            _core.Navigate(_app.Url);
            WebView.Focus(FocusState.Programmatic);
        }

        private async Task<string> BuildPageScriptAsync()
        {
            var config = new JsonObject();
            config.SetNamedValue("xbox", JsonValue.CreateBooleanValue(IsXbox));
            config.SetNamedValue("remote", JsonValue.CreateBooleanValue(_app.RemoteInput));

            var script = new StringBuilder();
            script.Append("(function (config) {\n'use strict';\n");
            script.Append(await ReadScriptAsync("prelude.js")).Append('\n');
            if (_extension != null)
            {
                script.Append(await _extension.GetPageScriptAsync()).Append('\n');
            }
            if (_app.RemoteInput && (_extension == null || !_extension.HandlesInput))
            {
                script.Append(await ReadScriptAsync("remote.js")).Append('\n');
            }
            script.Append("})(").Append(config.Stringify()).Append(");\n");
            return script.ToString();
        }

        internal static async Task<string> ReadScriptAsync(string path)
        {
            var uri = path.Contains("/") ? "ms-appx:///" + path : "ms-appx:///Web/Inject/" + path;
            var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
            return await FileIO.ReadTextAsync(file);
        }

        private void OnControllerAction(string action, bool repeat)
        {
            if (action == "view")
            {
                App.GoHome();
            }
            else if (!_app.RemoteInput)
            {
                // The pointer does the rest.
                if (action == "back" && _core != null && _core.CanGoBack)
                {
                    _core.GoBack();
                }
            }
            else
            {
                SendInput(action, repeat);
            }
        }

#if DEBUG
        /// <summary>A controller press, from Diagnostics/DevCommands.</summary>
        internal void Press(string action)
        {
            OnControllerAction(action, false);
        }
#endif

        private async void SendInput(string action, bool repeat)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("type", JsonValue.CreateStringValue("input"));
            payload.SetNamedValue("action", JsonValue.CreateStringValue(action));
            payload.SetNamedValue("repeat", JsonValue.CreateBooleanValue(repeat));
            try
            {
                if (_core != null && (action == "select" || action == "x"))
                {
                    // OK and play count as the user's own presses, as a key press would, so what
                    // the page does with them may go full screen or play with sound (X's videos).
                    await _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"0\",\"userGesture\":true}");
                }
                _core?.PostWebMessageAsJson(payload.Stringify());
            }
            catch (Exception ex)
            {
                Log.Write("Sending input failed: " + ex.Message);
            }
        }

        // The console's mouse pointer, on or off. A TV app that reads the controller itself turns it
        // on for the pages it can't drive (X's sign-in), and the console's keyboard with it.
        private void SetPointer(bool on)
        {
            _pointer = on;
            var pointer = on ? RequiresPointer.WhenFocused : RequiresPointer.Never;
            RequiresPointer = pointer;
            WebView.RequiresPointer = pointer;
        }

        private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonObject message;
            if (!JsonObject.TryParse(e.WebMessageAsJson, out message))
            {
                return;
            }
            var type = message.GetNamedValue("type", JsonValue.CreateNullValue());
            switch (type.ValueType == JsonValueType.String ? type.GetString() : null)
            {
                case "app.log":
                    var text = message.GetNamedValue("message", JsonValue.CreateNullValue());
                    if (text.ValueType == JsonValueType.String)
                    {
                        Log.Write(_app.Id + ": " + text.GetString());
                    }
                    break;
                case "app.exit":
                    Log.Write(_app.Id + " closed itself");
                    App.GoHome();
                    break;
                case "app.pointer":
                    var on = message.GetNamedValue("on", JsonValue.CreateNullValue());
                    if (_app.RemoteInput && _extension != null && _extension.HandlesInput &&
                        on.ValueType == JsonValueType.Boolean && on.GetBoolean() != _pointer)
                    {
                        Log.Write(_app.Id + (on.GetBoolean() ? " asked for the pointer" : " gave back the pointer"));
                        SetPointer(on.GetBoolean());
                        WebView.Focus(FocusState.Programmatic);
                    }
                    break;
            }
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // In a TV app ControllerBridge handles the controller. Stop XAML from also using its
            // buttons to move focus out of the web page, except on the load error screen and while
            // the app has the pointer.
            var key = (int)e.Key;
            var isController = (key >= (int)VirtualKey.GamepadA && key <= (int)VirtualKey.GamepadRightThumbstickLeft) ||
                (key >= (int)VirtualKey.NavigationView && key <= (int)VirtualKey.NavigationCancel);
            if (isController && _app.RemoteInput && !_pointer && LoadErrorPanel.Visibility == Visibility.Collapsed)
            {
                e.Handled = true;
                if (!IsInWebView(FocusManager.GetFocusedElement() as DependencyObject))
                {
                    WebView.Focus(FocusState.Programmatic);
                }
            }
        }

        private bool IsInWebView(DependencyObject element)
        {
            for (; element != null; element = VisualTreeHelper.GetParent(element))
            {
                if (element == WebView)
                {
                    return true;
                }
            }
            return false;
        }

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Uri uri;
            var allowed = Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                (_extension == null || _extension.AllowsNavigation(uri));
            if (!allowed)
            {
                Log.Write("Not opening " + Log.DescribeUrl(e.Uri));
                e.Cancel = true;
            }
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    Log.Write(_app.Id + " loaded");
                    Splash.Visibility = Visibility.Collapsed;
                    SplashRing.IsActive = false;
                    ShowHints();
                }
                EmulatePageFocus(sender);
            }
            else if (!_loaded && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                Log.Write(_app.Id + " failed to load: " + e.WebErrorStatus);
                ShowLoadError("Check the console's internet connection and try again. (" + e.WebErrorStatus + ")");
            }
        }

        // How to get back out, for a few seconds once the app has loaded.
        private void ShowHints()
        {
            Hints.Show("B", "Back", "View", "Home");
            HintsPanel.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                HintsPanel.Visibility = Visibility.Collapsed;
            };
            timer.Start();
        }

        // With no mouse pointer nothing ever clicks into a TV app, so the browser treats the page as
        // unfocused (document.hasFocus() is false).
        private static async void EmulatePageFocus(CoreWebView2 core)
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            }
            catch (Exception ex)
            {
                Log.Error("Page focus emulation failed", ex);
            }
        }

        // There's only one window: links that would open another open here instead.
        private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            sender.Navigate(e.Uri);
        }

        private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log.Write("WebView2 process failed: " + e.ProcessFailedKind);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                sender.Reload();
            }
            else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                ShowLoadError("The web view stopped unexpectedly.");
            }
        }

        private void ShowLoadError(string message)
        {
            if (_closed)
            {
                return;
            }
            LoadErrorTitle.Text = "Couldn't load " + _app.Name;
            LoadErrorText.Text = message;
            LoadErrorPanel.Visibility = Visibility.Visible;
            if (_controller != null)
            {
                _controller.Paused = true;
            }
            RetryButton.Focus(FocusState.Programmatic);
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            // A fresh page also recovers from a crashed browser process.
            Frame.Navigate(typeof(WebAppPage), _app);
            Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            App.GoHome();
        }
    }
}
