using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenBox.Diagnostics;

namespace OpenBox.Spelunky
{
    /// <summary>
    /// spelunky.dll (Spelunky/ at the top of the repo): the game, built by CI and packaged with the app.
    /// Only <see cref="Start"/> needs the UI thread. The callback comes on any thread.
    /// </summary>
    internal static class SpelunkyNative
    {
        private const string Dll = "spelunky.dll";

        /// <summary>What the callback reports (Spelunky/src/report.rs).</summary>
        internal enum Event
        {
            Log = 0,
            /// <summary>The game couldn't start, or stopped on an error.</summary>
            Failed = 1,
            /// <summary>The game ended: Quit on its title screen.</summary>
            Ended = 2,
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Callback(int kind, IntPtr message);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int spelunky_start(IntPtr visual, string assets, string data, uint width, uint height,
            float scale, Callback callback);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void spelunky_resize(uint width, uint height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void spelunky_stop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint spelunky_stats(byte[] text, uint length);

        /// <summary>
        /// Starts the game in <paramref name="visual"/> (a ContainerVisual's IUnknown) filling
        /// <paramref name="width"/> by <paramref name="height"/> pixels, at <paramref name="scale"/> pixels
        /// per effective pixel, with its files in <paramref name="assets"/> and its saves and log in
        /// <paramref name="data"/>. 0, or -1 if it couldn't (the callback has said why). On the UI thread.
        /// </summary>
        public static int Start(IntPtr visual, string assets, string data, uint width, uint height, float scale,
            Callback callback)
        {
#if DEBUG
            LoadDevBuild(data);
            if (_dev != null)
            {
                return _dev.Start(visual, assets, data, width, height, scale, callback);
            }
#endif
            return spelunky_start(visual, assets, data, width, height, scale, callback);
        }

        /// <summary>The visual's new size in pixels. On the UI thread.</summary>
        public static void Resize(uint width, uint height)
        {
#if DEBUG
            if (_dev != null)
            {
                _dev.Resize(width, height);
                return;
            }
#endif
            spelunky_resize(width, height);
        }

        /// <summary>Ends the game and waits for it. Its saves are already written.</summary>
        public static void Stop()
        {
#if DEBUG
            if (_dev != null)
            {
                _dev.Stop();
                return;
            }
#endif
            spelunky_stop();
        }

        /// <summary>Frames and ticks over the last second, for the log.</summary>
        public static string Stats()
        {
            var buffer = new byte[256];
            uint length;
#if DEBUG
            length = _dev != null ? _dev.Stats(buffer, (uint)buffer.Length) : spelunky_stats(buffer, (uint)buffer.Length);
#else
            length = spelunky_stats(buffer, (uint)buffer.Length);
#endif
            return Encoding.UTF8.GetString(buffer, 0, (int)length);
        }

        /// <summary>A UTF-8 string from the game.</summary>
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

#if DEBUG
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int StartFunction(IntPtr visual, string assets, string data, uint width, uint height,
            float scale, Callback callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResizeFunction(uint width, uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StopFunction();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint StatsFunction(byte[] text, uint length);

        private sealed class DevBuild
        {
            public StartFunction Start;
            public ResizeFunction Resize;
            public StopFunction Stop;
            public StatsFunction Stats;
        }

        private static DevBuild _dev;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// Debug builds: a spelunky.dll uploaded to LocalState\Spelunky\dev (Device Portal's file
        /// explorer) is used instead of the package's, so a new build of the game can be tried without
        /// building the app. Loaded once: restart OpenBox for a newer one.
        /// </summary>
        private static void LoadDevBuild(string dataFolder)
        {
            var path = Path.Combine(dataFolder, "dev", Dll);
            if (_dev != null || !File.Exists(path))
            {
                return;
            }
            const uint LoadWithAlteredSearchPath = 0x8;
            var module = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero)
            {
                Log.Write("Spelunky: the dev build " + path + " didn't load (error " + Marshal.GetLastWin32Error() + ")");
                return;
            }
            _dev = new DevBuild
            {
                Start = Marshal.GetDelegateForFunctionPointer<StartFunction>(GetProcAddress(module, "spelunky_start")),
                Resize = Marshal.GetDelegateForFunctionPointer<ResizeFunction>(GetProcAddress(module, "spelunky_resize")),
                Stop = Marshal.GetDelegateForFunctionPointer<StopFunction>(GetProcAddress(module, "spelunky_stop")),
                Stats = Marshal.GetDelegateForFunctionPointer<StatsFunction>(GetProcAddress(module, "spelunky_stats")),
            };
            Log.Write("Spelunky: using the dev build " + path);
        }
#endif
    }
}
