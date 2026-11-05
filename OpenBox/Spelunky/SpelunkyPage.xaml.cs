using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using OpenBox.Diagnostics;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.Spelunky
{
    /// <summary>
    /// Spelunky Classic, built in: the port of the original (spelunky.dll, from Spelunky/), shown in
    /// a composition visual over this page. The first time, the game's files are downloaded from its
    /// release (<see cref="SpelunkyAssets"/>). The controller is the game's; View goes home, which
    /// ends the game (its saves are kept), and B is the game's own button.
    /// </summary>
    public sealed partial class SpelunkyPage : Page, IBackHandler
    {
        // The game calls this from its own thread for as long as it runs, so it's never collected.
        private static readonly SpelunkyNative.Callback GameCallback = OnGameEvent;
        private static SpelunkyPage _current;
        // The UI thread's, for the game's thread to reach the page through: CoreWindow can't be asked
        // for from another thread while the UI thread waits for the game to stop.
        private static CoreDispatcher _dispatcher;

        private CancellationTokenSource _cancel;
        private DisplayRequest _displayRequest;
        private bool _running;
        private bool _waitingForSize;
        private uint _width, _height;

        public SpelunkyPage()
        {
            this.InitializeComponent();
            Hints.Show("View", "Home");
            Screen.SizeChanged += (s, e) => OnScreenSize();
            // Nothing here takes focus, so the page does: the controller's presses stay off XAML's focus.
            Loaded += (s, e) => Focus(FocusState.Programmatic);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _current = this;
            _dispatcher = Dispatcher;
            _cancel = new CancellationTokenSource();
            var cancel = _cancel.Token;
            if (!SpelunkyAssets.Ready)
            {
                Message.Text = "Downloading the game's files from its release on GitHub (about 18 MB). This happens once.";
                Details.Text = "Spelunky Classic is a fan-made port of Spelunky Classic by Derek Yu, an unofficial mod " +
                    "that isn't made or endorsed by Derek Yu or Mossmouth. github.com/851-labs/spelunky";
                Progress.Value = 0;
                Progress.Visibility = Visibility.Visible;
                SpelunkyAssets.Progress = v => Progress.Value = v;
                try
                {
                    // Leaving the page doesn't stop the download: coming back waits for the same one.
                    await SpelunkyAssets.DownloadAsync();
                }
                catch (Exception ex)
                {
                    if (!cancel.IsCancellationRequested)
                    {
                        Log.Error("Spelunky: downloading its files failed", ex);
                        ShowProblem("Couldn't download the game's files: " + ex.Message);
                    }
                    return;
                }
                Progress.Visibility = Visibility.Collapsed;
            }
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            Message.Text = "Starting…";
            Details.Text = string.Empty;
            // The panel needs its size first.
            _waitingForSize = true;
            _width = _height = 0;
            OnScreenSize();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _cancel?.Cancel();
            _waitingForSize = false;
            Stop();
            if (_current == this)
            {
                _current = null;
            }
        }

        // B is one of the game's buttons; View goes home.
        bool IBackHandler.HandleBack()
        {
            return true;
        }

        /// <summary>The display's pixels per effective pixel: 2 on the console.</summary>
        private static double DisplayScale()
        {
            var scale = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            return scale > 0 ? scale : 1;
        }

        private void OnScreenSize()
        {
            var scale = DisplayScale();
            var width = (uint)Math.Round(Screen.ActualWidth * scale);
            var height = (uint)Math.Round(Screen.ActualHeight * scale);
            if (width == 0 || height == 0 || (width == _width && height == _height))
            {
                return;
            }
            _width = width;
            _height = height;
            if (_running)
            {
                SpelunkyNative.Resize(width, height);
            }
            else if (_waitingForSize)
            {
                _waitingForSize = false;
                Start(width, height, scale);
            }
        }

        private void Start(uint width, uint height, double scale)
        {
            Log.Write("Spelunky: starting in " + width + "x" + height + " pixels at " + scale + "x");
            // The game puts its picture in a visual of its own over the Screen element.
            var compositor = ElementCompositionPreview.GetElementVisual(Screen).Compositor;
            var container = compositor.CreateContainerVisual();
            container.RelativeSizeAdjustment = Vector2.One;
            ElementCompositionPreview.SetElementChildVisual(Screen, container);
            var visual = Marshal.GetIUnknownForObject(container);
            int result;
            try
            {
                result = SpelunkyNative.Start(visual, SpelunkyAssets.AssetsFolder, SpelunkyAssets.DataFolder,
                    width, height, (float)scale, GameCallback);
            }
            catch (Exception ex)
            {
                // spelunky.dll missing or not loading.
                Log.Error("Spelunky: the game didn't load", ex);
                ShowProblem("The game didn't load: " + ex.Message);
                return;
            }
            finally
            {
                Marshal.Release(visual);
            }
            if (result != 0)
            {
                // The callback has shown why.
                return;
            }
            _running = true;
            StatusPanel.Visibility = Visibility.Collapsed;
            KeepScreenOn(true);
        }

        private void Stop()
        {
            KeepScreenOn(false);
            if (_running)
            {
                _running = false;
                Log.Write("Spelunky: " + SpelunkyNative.Stats());
                SpelunkyNative.Stop();
            }
            ElementCompositionPreview.SetElementChildVisual(Screen, null);
        }

        private void ShowProblem(string problem)
        {
            Progress.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            Title.Text = "Spelunky isn't working";
            Message.Text = problem;
            Details.Text = "OpenBox's log has more (LocalState\\Spelunky\\spelunky.log too).";
        }

        private static void OnGameEvent(int kind, IntPtr message)
        {
            var text = SpelunkyNative.FromUtf8(message);
            Log.Write("Spelunky: " + text);
            if (kind == (int)SpelunkyNative.Event.Log)
            {
                return;
            }
            var _ = _dispatcher?.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var page = _current;
                if (page == null)
                {
                    return;
                }
                if (kind == (int)SpelunkyNative.Event.Ended)
                {
                    // Quit on the title screen: like closing the game's window.
                    page.Stop();
                    App.GoHome();
                }
                else
                {
                    page.Stop();
                    page.ShowProblem(text);
                }
            });
        }

        // The TV shouldn't dim while the game's on.
        private void KeepScreenOn(bool on)
        {
            try
            {
                if (on && _displayRequest == null)
                {
                    _displayRequest = new DisplayRequest();
                    _displayRequest.RequestActive();
                }
                else if (!on && _displayRequest != null)
                {
                    _displayRequest.RequestRelease();
                    _displayRequest = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Spelunky: keeping the screen on failed", ex);
            }
        }
    }
}
