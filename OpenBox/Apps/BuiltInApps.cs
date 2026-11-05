using System;
using OpenBox.AirPlay;
using OpenBox.Spelunky;
using OpenBox.Store;
using OpenBox.YouTube;
using Windows.UI;

namespace OpenBox.Apps
{
    /// <summary>
    /// The apps that come with openbox. They come first on the homescreen, with the Store last,
    /// until tiles are moved; they can be moved but not uninstalled.
    ///
    /// A native app is a XAML page: add it to <see cref="Core"/> with <see cref="AppEntry.Native"/>
    /// and opening its tile navigates to that page (the app's entry is the navigation parameter).
    /// View goes home from it; B belongs to the app, which gets it by implementing IBackHandler.
    /// AirPlay is one.
    ///
    /// A native app can also come from the Store instead (a store entry's "native", see
    /// apps/README.md): built in, but on the homescreen only once added. Spelunky is one.
    /// </summary>
    internal static class BuiltInApps
    {
        public static readonly AppEntry YouTube = AppEntry.BuiltInWeb(
            "youtube", "YouTube", "ms-appx:///Assets/Apps/youtube.png", Color.FromArgb(255, 255, 0, 51),
            YouTubeExtension.Url, true, () => new YouTubeExtension());

        /// <summary>The console as an AirPlay receiver (AirPlay/): this shows what's mirrored to it.</summary>
        public static readonly AppEntry AirPlay = AppEntry.Native(
            "airplay", "AirPlay", "ms-appx:///Assets/Apps/airplay.png", Color.FromArgb(255, 10, 132, 255), typeof(AirPlayPage));

        public static readonly AppEntry Store = AppEntry.Native(
            "store", "Store", "ms-appx:///Assets/Apps/store.png", Color.FromArgb(255, 42, 45, 58), typeof(StorePage));

        /// <summary>The built-in apps before the installed ones, in homescreen order.</summary>
        public static readonly AppEntry[] Core = { YouTube, AirPlay };

        /// <summary>
        /// The page of a native app the Store can add, by its store entry's "native"; null for one
        /// this version doesn't have (a newer store's), which then isn't offered.
        /// </summary>
        internal static Type NativeStorePage(string native)
        {
            switch (native)
            {
                // Spelunky Classic, the port of the original (Spelunky/).
                case "spelunky": return typeof(SpelunkyPage);
                default: return null;
            }
        }

        /// <summary>Store apps can't use these ids (the store index build refuses them too).</summary>
        public static bool IsReservedId(string id)
        {
            return id == Store.Id || System.Array.Exists(Core, app => app.Id == id);
        }
    }
}
