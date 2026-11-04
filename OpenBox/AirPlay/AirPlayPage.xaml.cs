using System;
using OpenBox.Diagnostics;
using Windows.System.Display;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.AirPlay
{
    /// <summary>
    /// AirPlay, built in: the screen a Mac, iPhone or iPad mirrors to the console, full screen. With
    /// nothing mirrored, how to mirror. Opened from its tile, or by a sender starting to mirror
    /// (<see cref="Incoming"/>), in which case it goes back to where it was when the sender leaves.
    /// View goes home. B goes home too, unless something is being mirrored.
    /// </summary>
    public sealed partial class AirPlayPage : Page, IBackHandler
    {
        /// <summary>The navigation parameter when a sender opened the page.</summary>
        internal const string Incoming = "incoming";

        private readonly DispatcherTimer _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private DisplayRequest _displayRequest;
        private bool _openedBySender;
        private bool _wasMirroring;

        public AirPlayPage()
        {
            this.InitializeComponent();
            Hints.Show("B", "Back", "View", "Home");
            MirrorHints.Show("View", "Home");
            _hintTimer.Tick += (s, e) =>
            {
                _hintTimer.Stop();
                MirrorHintsPanel.Visibility = Visibility.Collapsed;
            };
            // Nothing here takes focus, so the page does: View reaches the app through it.
            Loaded += (s, e) => Focus(FocusState.Programmatic);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _openedBySender = e.Parameter as string == Incoming;
            Screen.SetMediaPlayer(AirPlayReceiver.Player.MediaPlayer);
            AirPlayReceiver.Changed += OnReceiverChanged;
            _wasMirroring = false;
            Update();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            AirPlayReceiver.Changed -= OnReceiverChanged;
            Screen.SetMediaPlayer(null);
            _hintTimer.Stop();
            KeepScreenOn(false);
        }

        bool IBackHandler.HandleBack()
        {
            if (AirPlayReceiver.State != AirPlayState.Mirroring)
            {
                App.GoHome();
            }
            return true;
        }

        private void OnReceiverChanged(object sender, EventArgs e)
        {
            if (_openedBySender && AirPlayReceiver.State == AirPlayState.Waiting)
            {
                // The sender that opened the page has gone: back to what was on before.
                Log.Write("AirPlay: the sender left, going back");
                App.GoBack();
                return;
            }
            Update();
        }

        private void Update()
        {
            var state = AirPlayReceiver.State;
            var mirroring = state == AirPlayState.Mirroring;
            WaitingPanel.Visibility = mirroring ? Visibility.Collapsed : Visibility.Visible;
            KeepScreenOn(mirroring);
            if (mirroring && !_wasMirroring)
            {
                MirrorHintsPanel.Visibility = Visibility.Visible;
                _hintTimer.Start();
            }
            else if (!mirroring)
            {
                MirrorHintsPanel.Visibility = Visibility.Collapsed;
            }
            _wasMirroring = mirroring;

            var name = AirPlayReceiver.Name;
            var address = AirPlayReceiver.Address();
            switch (state)
            {
                case AirPlayState.Waiting:
                case AirPlayState.Mirroring:
                    Title.Text = "Mirror your screen to " + name;
                    Message.Text = "On a Mac, iPhone or iPad, open Control Center, choose Screen Mirroring, then choose “" +
                        name + "”. Its screen and sound play here.";
                    Details.Text = "Your device needs to be on the same network as this console" +
                        (address != null ? " (" + address + ")." : ".");
                    break;
                case AirPlayState.Connected:
                    Title.Text = "Connected to " + AirPlayReceiver.SenderName;
                    Message.Text = "Its sound plays here. Choose Screen Mirroring on it to show its screen too.";
                    Details.Text = string.Empty;
                    break;
                case AirPlayState.Failed:
                    Title.Text = "AirPlay isn't working";
                    Message.Text = AirPlayReceiver.Problem ?? string.Empty;
                    Details.Text = "OpenBox's log has more.";
                    break;
                default:
                    Title.Text = "AirPlay";
                    Message.Text = "Starting…";
                    Details.Text = string.Empty;
                    break;
            }
        }

        // The TV shouldn't dim while something's mirrored.
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
                Log.Error("AirPlay: keeping the screen on failed", ex);
            }
        }
    }
}
