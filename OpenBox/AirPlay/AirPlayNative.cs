using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBox.AirPlay
{
    /// <summary>
    /// airplay.dll, the receiver (AirPlay/receiver.h): UxPlay's AirPlay server, built for Windows
    /// by CI and packaged with the app. Its callbacks come on the receiver's own threads.
    /// </summary>
    internal static class AirPlayNative
    {
        private const string Dll = "airplay.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(int level, IntPtr message);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ConnectedCallback(IntPtr name, IntPtr model);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void VideoStartedCallback(int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void VideoFrameCallback(IntPtr data, int length, long time, int isH265);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void AudioFrameCallback(IntPtr samples, int frames, int channels, int rate, long time);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void VolumeCallback(float decibels);

        /// <summary>airplay_callbacks_t: function pointers, in its order.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Callbacks
        {
            public IntPtr Log;
            public IntPtr Connected;
            public IntPtr Disconnected;
            public IntPtr VideoStarted;
            public IntPtr VideoFrame;
            public IntPtr VideoStopped;
            public IntPtr AudioFrame;
            public IntPtr Volume;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int airplay_start(byte[] name, byte[] deviceId, int width, int height, int fps, int port,
            ref Callbacks callbacks);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void airplay_stop();

        /// <summary>A string for the receiver: UTF-8, ending in a zero.</summary>
        public static byte[] Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        /// <summary>A UTF-8 string from the receiver (sender names have curly apostrophes and the like).</summary>
        public static string FromUtf8(IntPtr text)
        {
            if (text == IntPtr.Zero)
            {
                return string.Empty;
            }
            var length = 0;
            while (Marshal.ReadByte(text, length) != 0)
            {
                length++;
            }
            var bytes = new byte[length];
            Marshal.Copy(text, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
