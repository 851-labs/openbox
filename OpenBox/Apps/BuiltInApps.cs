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
    /// View goes home from it; B belongs to the page, which gets it by implementing IBackHandler.
    /// AirPlay is the next one.
    /// </summary>
    internal static class BuiltInApps
    {
        public static readonly AppEntry YouTube = AppEntry.BuiltInWeb(
            "youtube", "YouTube", "ms-appx:///Assets/Apps/youtube.png", Color.FromArgb(255, 255, 0, 51),
            YouTubeExtension.Url, true, () => new YouTubeExtension());

        public static readonly AppEntry Store = AppEntry.Native(
            "store", "Store", "ms-appx:///Assets/Apps/store.png", Color.FromArgb(255, 42, 45, 58), typeof(StorePage));

        /// <summary>The built-in apps before the installed ones, in homescreen order.</summary>
        public static readonly AppEntry[] Core = { YouTube };

        /// <summary>Store apps can't use these ids (the store index build refuses them too).</summary>
        public static bool IsReservedId(string id)
        {
            return id == Store.Id || id == "airplay" || System.Array.Exists(Core, app => app.Id == id);
        }
    }
}
