using System;
using System.Threading.Tasks;
using OpenBox.AdBlock;
using OpenBox.Apps;
using OpenBox.Diagnostics;
using OpenBox.Home;
using OpenBox.Packages;
using OpenBox.Store;
using OpenBox.Web;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace OpenBox
{
    sealed partial class App : Application
    {
        private static Frame _frame;

        public App()
        {
            Log.Start();
            Log.Write("openbox starting, version " + VersionText());
            this.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, e) => Log.Error("Unobserved task exception", e.Exception);

            // WebView2 reads these when it starts its browser process, so they must be set before the
            // first page loads. Black instead of white while an app loads, and videos may start with
            // sound without a click: in TV apps the controller's presses reach the page as script
            // events, which the browser doesn't count as the user interacting.
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF000000");
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--autoplay-policy=no-user-gesture-required");

            this.InitializeComponent();
            // Xbox: the controller moves XAML's focus; only web pages that want it get the mouse pointer.
            this.RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            this.FocusVisualKind = FocusVisualKind.HighVisibility;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Xbox: use the whole screen instead of the TV-safe area. Pages keep their own margins,
            // and web apps go edge to edge.
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);

            if (_frame == null)
            {
                // The shell's background, not black, shows between pages.
                _frame = new Frame { Background = new SolidColorBrush(Color.FromArgb(255, 17, 17, 20)) };
                _frame.NavigationFailed += (s, args) => { throw new Exception("Failed to load page " + args.SourcePageType.FullName); };
                Window.Current.Content = _frame;
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
                // View goes home from anywhere: it's the one way back to the homescreen. (In web
                // apps, which have the focus inside the web view, WebAppPage reads it from the
                // controller.)
                _frame.PreviewKeyDown += (s, args) =>
                {
                    if (args.Key == VirtualKey.GamepadView)
                    {
                        args.Handled = true;
                        GoHome();
                    }
                };
            }

            if (e.PrelaunchActivated == false)
            {
                if (_frame.Content == null)
                {
                    try
                    {
                        _frame.Navigate(typeof(HomePage));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("The homescreen failed to load", ex);
                        ShowFatalError(ex);
                    }
                }
                Window.Current.Activate();
                // The ad filters take a moment to load: have them ready by the time an app opens.
                AdFilters.Preload();
                StoreIndex.RefreshInstalled();
#if DEBUG
                DevCommands.Start();
#endif
            }
        }

        /// <summary>Opens an app from the homescreen.</summary>
        internal static async void Open(AppEntry app)
        {
            if (!app.IsPackage)
            {
                _frame.Navigate(app.PageType ?? typeof(WebAppPage), app);
                return;
            }
            // An app of its own: the console switches to it, and openbox stays where it is.
            var problem = await PackageApps.LaunchAsync(app);
            if (problem != null)
            {
                await new ContentDialog
                {
                    Title = "Couldn't open " + app.Name,
                    Content = problem,
                    CloseButtonText = "OK",
                }.ShowAsync();
            }
        }

        /// <summary>What's on screen: the homescreen, an app's page, the Store.</summary>
        internal static object CurrentPage
        {
            get { return _frame?.Content; }
        }

        /// <summary>Back to the homescreen, from wherever.</summary>
        internal static void GoHome()
        {
            while (_frame.BackStack.Count > 1)
            {
                _frame.BackStack.RemoveAt(_frame.BackStack.Count - 1);
            }
            if (_frame.CanGoBack)
            {
                _frame.GoBack();
            }
        }

        // B, on the console. It belongs to whatever is open: in openbox's own screens (the
        // Store, menus) it's back navigation; in an app it's the app's, and View goes home.
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // Handled either way: unhandled, B would close openbox.
            e.Handled = true;
            (_frame.Content as IBackHandler)?.HandleBack();
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Write("Unhandled exception: " + e.Message + Environment.NewLine + e.Exception);
            e.Handled = true;
            ShowFatalError(e.Exception);
        }

        // Shows what went wrong instead of leaving the console on the splash screen.
        private static void ShowFatalError(Exception error)
        {
            var text = new TextBlock
            {
                Text = "openbox hit an error and can't continue:" + Environment.NewLine + Environment.NewLine +
                    "Version " + VersionText() + Environment.NewLine + error,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(48),
            };
            Window.Current.Content = new ScrollViewer
            {
                Content = text,
                Background = new SolidColorBrush(Colors.Black),
            };
            Window.Current.Activate();
        }

        internal static string VersionText()
        {
            var version = Package.Current.Id.Version;
            return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
        }
    }
}
