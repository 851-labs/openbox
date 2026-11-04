using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using OpenBox.Diagnostics;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Render;

namespace OpenBox.AirPlay
{
    /// <summary>
    /// Plays what a sender mirrors: its H.264 through a MediaStreamSource (the console decodes it in
    /// hardware), shown by whatever MediaPlayerElement has <see cref="MediaPlayer"/>, and its audio
    /// through an AudioGraph. Both play as soon as they arrive, for the least delay. Frames and audio
    /// come in on the receiver's threads.
    /// </summary>
    internal sealed class MirrorPlayer
    {
        // Frames waiting for the decoder. More than this and it's fallen behind: skip to the next
        // key frame rather than show the past.
        private const int MaxQueuedFrames = 90;
        // Audio queued beyond this (in samples) is dropped, so the sound doesn't drift behind.
        private const int MaxQueuedAudio = 44100 / 4;

        private readonly object _lock = new object();
        private readonly Queue<MediaStreamSample> _frames = new Queue<MediaStreamSample>();
        private MediaStreamSource _source;
        private MediaStreamSourceSampleRequest _waitingRequest;
        private MediaStreamSourceSampleRequestDeferral _waitingDeferral;
        private bool _haveKeyFrame;
        private long _firstTime;
        private long _lastTicks;
        private long _frameCount;

        private AudioGraph _graph;
        private AudioFrameInputNode _audioInput;
        private Task _audioStarting;
        private double _gain = 1;

        public MirrorPlayer()
        {
            MediaPlayer = new MediaPlayer
            {
                // A live stream: no buffering ahead, late frames dropped.
                RealTimePlayback = true,
                AutoPlay = true,
                AudioCategory = MediaPlayerAudioCategory.Media,
            };
            MediaPlayer.MediaFailed += (s, e) => Log.Write("AirPlay: video failed: " + e.Error + " " + e.ErrorMessage);
        }

        public MediaPlayer MediaPlayer { get; private set; }

        /// <summary>Video starts (or the sender's screen changed size): a new stream.</summary>
        public void StartVideo(int width, int height)
        {
            var properties = VideoEncodingProperties.CreateH264();
            properties.Width = (uint)width;
            properties.Height = (uint)height;
            var source = new MediaStreamSource(new VideoStreamDescriptor(properties))
            {
                BufferTime = TimeSpan.Zero,
                CanSeek = false,
                IsLive = true,
            };
            source.Starting += (s, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            source.SampleRequested += OnSampleRequested;
            lock (_lock)
            {
                EndStreamLocked();
                _source = source;
                _haveKeyFrame = false;
                _frameCount = 0;
            }
            MediaPlayer.Source = MediaSource.CreateFromMediaStreamSource(source);
            MediaPlayer.Play();
            Log.Write(string.Format("AirPlay: video stream {0}x{1}", width, height));
        }

        public void StopVideo()
        {
            lock (_lock)
            {
                EndStreamLocked();
            }
            MediaPlayer.Source = null;
        }

        /// <summary>One frame, Annex B. time is in nanoseconds (the receiver's clock).</summary>
        public void PushVideo(IntPtr data, int length, long time)
        {
            var keyFrame = IsKeyFrame(data, length);
            lock (_lock)
            {
                if (_source == null)
                {
                    return;
                }
                if (!_haveKeyFrame)
                {
                    // The decoder needs the SPS and PPS that come with a key frame first.
                    if (!keyFrame)
                    {
                        return;
                    }
                    _haveKeyFrame = true;
                    _firstTime = time;
                    _lastTicks = -1;
                }
                if (_frames.Count >= MaxQueuedFrames)
                {
                    if (!keyFrame)
                    {
                        return;
                    }
                    Log.Write("AirPlay: the decoder fell behind; skipped " + _frames.Count + " frames");
                    _frames.Clear();
                }
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                // In 100 ns units from the first frame, always going forward.
                var ticks = Math.Max((time - _firstTime) / 100, _lastTicks + 1);
                _lastTicks = ticks;
                var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), TimeSpan.FromTicks(ticks));
                sample.KeyFrame = keyFrame;
                if (++_frameCount == 1)
                {
                    Log.Write("AirPlay: first frame, " + length + " bytes");
                }
                if (_waitingRequest != null)
                {
                    _waitingRequest.Sample = sample;
                    _waitingDeferral.Complete();
                    _waitingRequest = null;
                    _waitingDeferral = null;
                }
                else
                {
                    _frames.Enqueue(sample);
                }
            }
        }

        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            lock (_lock)
            {
                if (sender != _source)
                {
                    return; // An old stream: no sample ends it.
                }
                if (_frames.Count > 0)
                {
                    args.Request.Sample = _frames.Dequeue();
                    return;
                }
                // Nothing yet: answer when the next frame arrives.
                _waitingRequest = args.Request;
                _waitingDeferral = args.Request.GetDeferral();
            }
        }

        private void EndStreamLocked()
        {
            _frames.Clear();
            if (_waitingDeferral != null)
            {
                // No sample: the end of the stream.
                _waitingDeferral.Complete();
                _waitingRequest = null;
                _waitingDeferral = null;
            }
            if (_source != null)
            {
                _source.SampleRequested -= OnSampleRequested;
                _source = null;
            }
        }

        /// <summary>An IDR slice (5) or SPS (7) after any of the frame's start codes.</summary>
        private static bool IsKeyFrame(IntPtr data, int length)
        {
            var scan = Math.Min(length, 512);
            for (var i = 0; i + 3 < scan; i++)
            {
                if (Marshal.ReadByte(data, i) == 0 && Marshal.ReadByte(data, i + 1) == 0 && Marshal.ReadByte(data, i + 2) == 1)
                {
                    var type = Marshal.ReadByte(data, i + 3) & 0x1F;
                    if (type == 5 || type == 7)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Audio.

        /// <summary>Sets up the audio output, once; audio that comes before it's ready is dropped.</summary>
        public Task StartAudioAsync()
        {
            lock (_lock)
            {
                if (_audioStarting == null)
                {
                    _audioStarting = CreateAudioGraphAsync();
                }
                return _audioStarting;
            }
        }

        private async Task CreateAudioGraphAsync()
        {
            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.Media)
                {
                    QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
                };
                var created = await AudioGraph.CreateAsync(settings);
                if (created.Status != AudioGraphCreationStatus.Success)
                {
                    Log.Write("AirPlay: no audio output: " + created.Status);
                    return;
                }
                var graph = created.Graph;
                var output = await graph.CreateDeviceOutputNodeAsync();
                if (output.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    Log.Write("AirPlay: no audio output device: " + output.Status);
                    graph.Dispose();
                    return;
                }
                // What the receiver decodes to: 44.1 kHz stereo floats. The graph converts it.
                var format = AudioEncodingProperties.CreatePcm(44100, 2, 32);
                format.Subtype = MediaEncodingSubtypes.Float;
                var input = graph.CreateFrameInputNode(format);
                input.OutgoingGain = _gain;
                input.AddOutgoingConnection(output.DeviceOutputNode);
                graph.Start();
                lock (_lock)
                {
                    _graph = graph;
                    _audioInput = input;
                }
                Log.Write("AirPlay: audio output ready, " + graph.EncodingProperties.SampleRate + " Hz");
            }
            catch (Exception ex)
            {
                Log.Error("AirPlay: audio output failed", ex);
            }
        }

        /// <summary>Interleaved 32-bit float samples.</summary>
        public unsafe void PushAudio(IntPtr samples, int frames, int channels, int rate)
        {
            AudioFrameInputNode input;
            lock (_lock)
            {
                input = _audioInput;
            }
            if (input == null || channels != 2 || rate != 44100)
            {
                return;
            }
            if (input.QueuedSampleCount > MaxQueuedAudio)
            {
                input.DiscardQueuedFrames();
            }
            var bytes = (uint)(frames * channels * sizeof(float));
            var frame = new AudioFrame(bytes);
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                byte* destination;
                uint capacity;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out destination, out capacity);
                Buffer.MemoryCopy((void*)samples, destination, capacity, Math.Min(bytes, capacity));
            }
            input.AddFrame(frame);
        }

        /// <summary>The sender's volume slider, 0 to 1: AirPlay's -30 to 0 dB, and 0 for muted.</summary>
        public void SetVolume(float volume)
        {
            _gain = volume <= 0 ? 0 : Math.Pow(10, (volume * 30 - 30) / 20);
            lock (_lock)
            {
                if (_audioInput != null)
                {
                    _audioInput.OutgoingGain = _gain;
                }
            }
        }

        /// <summary>The sender's gone: drop what's queued.</summary>
        public void Reset()
        {
            StopVideo();
            lock (_lock)
            {
                _audioInput?.DiscardQueuedFrames();
            }
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }
    }
}
