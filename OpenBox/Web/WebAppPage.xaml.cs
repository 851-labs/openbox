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
using Windows.UI.Core;
using Windows.UI.ViewManagement;
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
    /// page) and View goes home.
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
        private InputPane _inputPane;
        private bool _keyboardUp;
        private char _highSurrogate;
        private Task _typing = Task.CompletedTask;

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
            var pointer = _app.RemoteInput ? RequiresPointer.Never : RequiresPointer.WhenFocused;
            RequiresPointer = pointer;
            WebView.RequiresPointer = pointer;
            // While the console's keyboard is up the controller is the keyboard's: a TV app doesn't
            // also get its presses (X's sign-in would move off the text box being typed in).
            _inputPane = InputPane.GetForCurrentView();
            _inputPane.Showing += OnKeyboardShowing;
            _inputPane.Hiding += OnKeyboardHiding;
            var window = Window.Current.CoreWindow;
            window.KeyDown += OnWindowKeyDown;
            window.CharacterReceived += OnWindowCharacterReceived;
            StartAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _closed = true;
            _inputPane.Showing -= OnKeyboardShowing;
            _inputPane.Hiding -= OnKeyboardHiding;
            var window = Window.Current.CoreWindow;
            window.KeyDown -= OnWindowKeyDown;
            window.CharacterReceived -= OnWindowCharacterReceived;
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
                ShowLoadError("OpenBox couldn't prepare " + _app.Name + ". " + ex.Message);
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
        /// <summary>A controller press, from Diagnostics/DevCommands: as if from the controller.</summary>
        internal void Press(string action)
        {
            _controller?.Press(action);
        }

        /// <summary>Closes the console's keyboard, from Diagnostics/DevCommands, as its B button would.</summary>
        internal bool HideKeyboard()
        {
            return _inputPane.TryHide();
        }

        /// <summary>Runs script in the page, from Diagnostics/DevCommands: its result, as JSON.</summary>
        internal async Task<string> RunScriptAsync(string script)
        {
            return _core == null ? "(not loaded)" : await _core.ExecuteScriptAsync(script);
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

        private void OnKeyboardShowing(InputPane sender, InputPaneVisibilityEventArgs e)
        {
            Log.Write("Keyboard up");
            _keyboardUp = true;
            if (_controller != null)
            {
                _controller.Paused = true;
            }
        }

        // The keyboard's own B (or its accept button) closes it. On the console InputPane.Visible
        // and the occluded area stay empty while it's up, so these two events are all there is.
        private void OnKeyboardHiding(InputPane sender, InputPaneVisibilityEventArgs e)
        {
            Log.Write("Keyboard down");
            _keyboardUp = false;
            _highSurrogate = '\0';
            if (_controller != null)
            {
                _controller.Paused = LoadErrorPanel.Visibility == Visibility.Visible;
            }
        }

        // What the console's keyboard types arrives at the app's window (CoreWindow), not at the
        // browser's input window, and WinUI's WebView2 passes nothing on from there but Tab, so the
        // page never sees it. While the keyboard is up its characters and editing keys are handed
        // to the page here, as the browser's own (trusted) input, so X's fields take them as typing.
        // Keys the browser's window gets itself never reach CoreWindow: nothing is typed twice.
        private void OnWindowKeyDown(CoreWindow sender, KeyEventArgs e)
        {
            string key;
            switch (e.VirtualKey)
            {
                case VirtualKey.Back: key = "Backspace"; break;
                case VirtualKey.Enter: key = "Enter"; break;
                case VirtualKey.Delete: key = "Delete"; break;
                case VirtualKey.Left: key = "ArrowLeft"; break;
                case VirtualKey.Right: key = "ArrowRight"; break;
                case VirtualKey.Home: key = "Home"; break;
                case VirtualKey.End: key = "End"; break;
                default: return;
            }
            if (!IsTypingInPage())
            {
                return;
            }
            e.Handled = true;
            var down = new JsonObject();
            // Enter as a key with text, so it submits a form as a typed Enter would.
            down.SetNamedValue("type", JsonValue.CreateStringValue(key == "Enter" ? "keyDown" : "rawKeyDown"));
            down.SetNamedValue("key", JsonValue.CreateStringValue(key));
            down.SetNamedValue("code", JsonValue.CreateStringValue(key));
            down.SetNamedValue("windowsVirtualKeyCode", JsonValue.CreateNumberValue((int)e.VirtualKey));
            down.SetNamedValue("nativeVirtualKeyCode", JsonValue.CreateNumberValue((int)e.VirtualKey));
            if (key == "Enter")
            {
                down.SetNamedValue("text", JsonValue.CreateStringValue("\r"));
            }
            var up = JsonObject.Parse(down.Stringify());
            up.SetNamedValue("type", JsonValue.CreateStringValue("keyUp"));
            up.Remove("text");
            TypeIntoPage("Input.dispatchKeyEvent", down.Stringify());
            TypeIntoPage("Input.dispatchKeyEvent", up.Stringify());
        }

        private void OnWindowCharacterReceived(CoreWindow sender, CharacterReceivedEventArgs e)
        {
            // Control characters (Backspace, Enter) come as keys, above.
            if (e.KeyCode < 32 || e.KeyCode == 127 || e.KeyCode > 0xFFFF || !IsTypingInPage())
            {
                return;
            }
            e.Handled = true;
            var c = (char)e.KeyCode;
            if (char.IsHighSurrogate(c))
            {
                _highSurrogate = c;
                return;
            }
            var text = char.IsLowSurrogate(c) && _highSurrogate != '\0' ? new string(new[] { _highSurrogate, c }) : c.ToString();
            _highSurrogate = '\0';
            var insert = new JsonObject();
            insert.SetNamedValue("text", JsonValue.CreateStringValue(text));
            TypeIntoPage("Input.insertText", insert.Stringify());
        }

        private bool IsTypingInPage()
        {
            return _keyboardUp && _core != null && IsInWebView(FocusManager.GetFocusedElement() as DependencyObject);
        }

        // One after another, so fast typing arrives in order.
        private void TypeIntoPage(string method, string parameters)
        {
            _typing = TypeAfterAsync(_typing, method, parameters);
        }

        private async Task TypeAfterAsync(Task previous, string method, string parameters)
        {
            await previous;
            try
            {
                if (_core != null)
                {
                    await _core.CallDevToolsProtocolMethodAsync(method, parameters);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Typing into the page failed: " + ex.Message);
            }
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
            }
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // In a TV app ControllerBridge handles the controller. Stop XAML from also using its
            // buttons to move focus out of the web page, except on the load error screen.
            var key = (int)e.Key;
            var isController = (key >= (int)VirtualKey.GamepadA && key <= (int)VirtualKey.GamepadRightThumbstickLeft) ||
                (key >= (int)VirtualKey.NavigationView && key <= (int)VirtualKey.NavigationCancel);
            if (isController && _app.RemoteInput && LoadErrorPanel.Visibility == Visibility.Collapsed)
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
