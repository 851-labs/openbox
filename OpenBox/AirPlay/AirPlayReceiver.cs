using System;
using System.Linq;
using System.Threading.Tasks;
using OpenBox.Diagnostics;
using Windows.ApplicationModel.Core;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.UI.Core;

namespace OpenBox.AirPlay
{
    /// <summary>
    /// openbox as an AirPlay receiver, for as long as it runs: Macs, iPhones and iPads on the
    /// network list it as <see cref="Name"/> under Screen Mirroring and AirPlay. A sender connecting
    /// opens <see cref="AirPlayPage"/>, which shows <see cref="Player"/>.
    /// </summary>
    internal static class AirPlayReceiver
    {
        public const string Name = "Xbox";
        // What senders are asked to mirror at: the console's output.
        private const int Width = 1920;
        private const int Height = 1080;
        private const int FramesPerSecond = 60;
        private const int Port = 7000;
        private const string DeviceIdKey = "airplayDeviceId";

        // Kept alive here: the receiver holds pointers to them.
        private static readonly AirPlayNative.LogCallback LogCallback = OnLog;
        private static readonly AirPlayNative.ConnectedCallback ConnectedCallback = OnConnected;
        private static readonly AirPlayNative.EventCallback DisconnectedCallback = OnDisconnected;
        private static readonly AirPlayNative.VideoStartedCallback VideoStartedCallback = OnVideoStarted;
        private static readonly AirPlayNative.VideoFrameCallback VideoFrameCallback = OnVideoFrame;
        private static readonly AirPlayNative.EventCallback VideoStoppedCallback = OnVideoStopped;
        private static readonly AirPlayNative.AudioFrameCallback AudioFrameCallback = OnAudioFrame;
        private static readonly AirPlayNative.VolumeCallback VolumeCallback = OnVolume;

        private static readonly object Sync = new object();
        private static CoreDispatcher _dispatcher;
        private static Task _running = Task.CompletedTask;
        private static bool _started;

        public static readonly MirrorPlayer Player = new MirrorPlayer();

        /// <summary>Whether senders can find the console, and what's happening.</summary>
        public static AirPlayState State { get; private set; }
        /// <summary>The connected sender's name ("Dylan's Mac Studio").</summary>
        public static string SenderName { get; private set; }
        /// <summary>Why the receiver isn't running, when it isn't.</summary>
        public static string Problem { get; private set; }

        /// <summary>A change in <see cref="State"/>, on the UI thread.</summary>
        public static event EventHandler Changed;

        /// <summary>A sender has started mirroring, on the UI thread: time to show it.</summary>
        public static event EventHandler MirroringStarted;

        /// <summary>Starts advertising, in the background. Called when openbox starts and resumes.</summary>
        public static void Start(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            lock (Sync)
            {
                if (_started)
                {
                    return;
                }
                _started = true;
                _running = _running.ContinueWith(_ => StartNative(), TaskScheduler.Default);
            }
        }

        /// <summary>Stops advertising and drops any sender (openbox is being suspended).</summary>
        public static Task StopAsync()
        {
            lock (Sync)
            {
                if (!_started)
                {
                    return _running;
                }
                _started = false;
                _running = _running.ContinueWith(_ =>
                {
                    try
                    {
                        AirPlayNative.airplay_stop();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("AirPlay: stopping failed", ex);
                    }
                    SetState(AirPlayState.Stopped, null);
                    Player.Reset();
                }, TaskScheduler.Default);
                return _running;
            }
        }

        private static void StartNative()
        {
            SetState(AirPlayState.Starting, null);
            try
            {
                var callbacks = new AirPlayNative.Callbacks
                {
                    Log = Pointer(LogCallback),
                    Connected = Pointer(ConnectedCallback),
                    Disconnected = Pointer(DisconnectedCallback),
                    VideoStarted = Pointer(VideoStartedCallback),
                    VideoFrame = Pointer(VideoFrameCallback),
                    VideoStopped = Pointer(VideoStoppedCallback),
                    AudioFrame = Pointer(AudioFrameCallback),
                    Volume = Pointer(VolumeCallback),
                };
                var result = AirPlayNative.airplay_start(AirPlayNative.Utf8(Name), AirPlayNative.Utf8(DeviceId()),
                    Width, Height, FramesPerSecond, Port, ref callbacks);
                if (result != 0)
                {
                    Problem = "The receiver couldn't start (error " + result + ").";
                    SetState(AirPlayState.Failed, null);
                    return;
                }
                Problem = null;
                SetState(AirPlayState.Waiting, null);
            }
            catch (Exception ex)
            {
                // A build without airplay.dll, say.
                Log.Error("AirPlay: the receiver didn't load", ex);
                Problem = "The receiver didn't load: " + ex.Message;
                SetState(AirPlayState.Failed, null);
            }
        }

        private static IntPtr Pointer(Delegate callback)
        {
            return System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(callback);
        }

        /// <summary>
        /// The console's AirPlay identity, a random MAC-style address made once and kept: senders
        /// remember receivers by it.
        /// </summary>
        private static string DeviceId()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            object saved;
            if (settings.TryGetValue(DeviceIdKey, out saved) && saved is string)
            {
                return (string)saved;
            }
            var bytes = new byte[6];
            new Random().NextBytes(bytes);
            // Locally administered and unicast, as a made-up address should be.
            bytes[0] = (byte)((bytes[0] & 0xFE) | 0x02);
            var id = string.Join(":", bytes.Select(b => b.ToString("x2")));
            settings[DeviceIdKey] = id;
            return id;
        }

        /// <summary>The console's address on the network, to show next to its name.</summary>
        public static string Address()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                var adapter = profile?.NetworkAdapter?.NetworkAdapterId;
                var host = NetworkInformation.GetHostNames().FirstOrDefault(name =>
                    name.Type == HostNameType.Ipv4 && name.IPInformation?.NetworkAdapter != null &&
                    (adapter == null || name.IPInformation.NetworkAdapter.NetworkAdapterId == adapter));
                return host?.DisplayName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetState(AirPlayState state, string sender)
        {
            var mirroringStarted = state == AirPlayState.Mirroring && State != AirPlayState.Mirroring;
            State = state;
            if (sender != null || state == AirPlayState.Waiting)
            {
                SenderName = sender;
            }
            OnUiThread(() =>
            {
                Changed?.Invoke(null, EventArgs.Empty);
                if (mirroringStarted)
                {
                    MirroringStarted?.Invoke(null, EventArgs.Empty);
                }
            });
        }

        private static void OnUiThread(Action action)
        {
            var dispatcher = _dispatcher ?? CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }
            var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("AirPlay: updating the screen failed", ex);
                }
            });
        }

        // The receiver's callbacks, on its threads. Nothing may be thrown back into it.

        private static void OnLog(int level, IntPtr message)
        {
            // Level 0 is UxPlay's debug output, with every request and packet: too much to keep.
            if (level > 0)
            {
                Log.Write("AirPlay: " + AirPlayNative.FromUtf8(message));
            }
        }

        private static void OnConnected(IntPtr name, IntPtr model)
        {
            try
            {
                var sender = AirPlayNative.FromUtf8(name);
                SetState(AirPlayState.Connected, string.IsNullOrEmpty(sender) ? "A device" : sender);
                var ignored = Player.StartAudioAsync();
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: connecting failed", ex);
            }
        }

        private static void OnDisconnected()
        {
            try
            {
                Player.Reset();
                SetState(AirPlayState.Waiting, null);
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: disconnecting failed", ex);
            }
        }

        private static void OnVideoStarted(int width, int height)
        {
            try
            {
                Player.StartVideo(width, height);
                SetState(AirPlayState.Mirroring, SenderName);
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: starting the video failed", ex);
            }
        }

        private static void OnVideoFrame(IntPtr data, int length, long time, int isH265)
        {
            try
            {
                Player.PushVideo(data, length, time);
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: a frame failed", ex);
            }
        }

        private static void OnVideoStopped()
        {
            try
            {
                Player.StopVideo();
                if (State == AirPlayState.Mirroring)
                {
                    SetState(AirPlayState.Connected, SenderName);
                }
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: stopping the video failed", ex);
            }
        }

        private static void OnAudioFrame(IntPtr samples, int frames, int channels, int rate, long time)
        {
            try
            {
                Player.PushAudio(samples, frames, channels, rate);
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: audio failed", ex);
            }
        }

        private static void OnVolume(float decibels)
        {
            try
            {
                Player.SetVolume(decibels);
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: setting the volume failed", ex);
            }
        }
    }

    internal enum AirPlayState
    {
        Stopped,
        Starting,
        /// <summary>Advertised, with no sender.</summary>
        Waiting,
        /// <summary>A sender is connected, but not mirroring (yet, or any more).</summary>
        Connected,
        Mirroring,
        Failed,
    }
}
