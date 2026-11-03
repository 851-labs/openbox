using System;
using System.Threading.Tasks;
using OpenBox.Diagnostics;
using OpenBox.Packages;
using Windows.UI.Xaml.Controls;

namespace OpenBox.Apps
{
    /// <summary>Taking a store app off the console, the same way from the Store and the homescreen.</summary>
    internal static class AppActions
    {
        /// <summary>
        /// Removes the app's tile, and a package app's package. Package apps always ask first
        /// (their saves go with them); web apps only when <paramref name="confirm"/> is set.
        /// True if it was removed.
        /// </summary>
        public static async Task<bool> UninstallAsync(AppEntry app, bool confirm)
        {
            if (app.IsPackage || confirm)
            {
                var dialog = new ContentDialog
                {
                    Title = "Uninstall " + app.Name + "?",
                    Content = app.IsPackage
                        ? "This removes it from the console, with everything saved in it."
                        : "It comes off your homescreen. You can add it again from the Store.",
                    PrimaryButtonText = "Uninstall",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return false;
                }
            }
            if (app.IsPackage)
            {
                try
                {
                    await PackageApps.UninstallAsync(app);
                }
                catch (Exception ex)
                {
                    Log.Error("Uninstalling " + app.Id + " failed", ex);
                    await new ContentDialog
                    {
                        Title = "Couldn't uninstall " + app.Name,
                        Content = ex.Message,
                        CloseButtonText = "OK",
                    }.ShowAsync();
                    return false;
                }
            }
            await AppLibrary.UninstallAsync(app.Id);
            return true;
        }
    }
}
