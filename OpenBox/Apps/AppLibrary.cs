using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenBox.Diagnostics;
using Windows.Data.Json;
using Windows.Storage;

namespace OpenBox.Apps
{
    /// <summary>
    /// The store apps added to the homescreen, kept in LocalState/apps.json with everything needed
    /// to open them, so their tiles work without reaching the store.
    /// </summary>
    internal static class AppLibrary
    {
        private const string FileName = "apps.json";
        private static List<AppEntry> _installed;

        public static async Task<IReadOnlyList<AppEntry>> GetInstalledAsync()
        {
            if (_installed == null)
            {
                _installed = await LoadAsync();
            }
            return _installed;
        }

        /// <summary>Everything on the homescreen, in order.</summary>
        public static async Task<List<AppEntry>> GetHomeAppsAsync()
        {
            var apps = new List<AppEntry>(BuiltInApps.Core);
            apps.AddRange(await GetInstalledAsync());
            apps.Add(BuiltInApps.Store);
            return apps;
        }

        public static bool IsInstalled(string id)
        {
            return _installed != null && _installed.Any(app => app.Id == id);
        }

        public static async Task InstallAsync(AppEntry app)
        {
            await GetInstalledAsync();
            _installed.RemoveAll(existing => existing.Id == app.Id);
            _installed.Add(app);
            Log.Write("Added " + app.Id + " to the homescreen");
            await SaveAsync();
        }

        public static async Task UninstallAsync(string id)
        {
            await GetInstalledAsync();
            _installed.RemoveAll(existing => existing.Id == id);
            Log.Write("Removed " + id + " from the homescreen");
            await SaveAsync();
        }

        /// <summary>Brings installed apps up to date with the store (a new address or icon, say).</summary>
        public static async Task UpdateFromStoreAsync(IEnumerable<AppEntry> storeApps)
        {
            await GetInstalledAsync();
            var changed = false;
            foreach (var app in storeApps)
            {
                var index = _installed.FindIndex(existing => existing.Id == app.Id);
                if (index >= 0 && _installed[index].ToJson().Stringify() != app.ToJson().Stringify())
                {
                    _installed[index] = app;
                    changed = true;
                }
            }
            if (changed)
            {
                await SaveAsync();
            }
        }

        private static async Task<List<AppEntry>> LoadAsync()
        {
            var result = new List<AppEntry>();
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                if (!File.Exists(path))
                {
                    return result;
                }
                var file = await StorageFile.GetFileFromPathAsync(path);
                JsonArray array;
                if (!JsonArray.TryParse(await FileIO.ReadTextAsync(file), out array))
                {
                    Log.Write("apps.json isn't valid; starting with no store apps");
                    return result;
                }
                foreach (var value in array)
                {
                    var app = value.ValueType == JsonValueType.Object ? AppEntry.FromJson(value.GetObject()) : null;
                    if (app != null && !BuiltInApps.IsReservedId(app.Id))
                    {
                        result.Add(app);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Reading apps.json failed", ex);
            }
            return result;
        }

        private static async Task SaveAsync()
        {
            var array = new JsonArray();
            foreach (var app in _installed)
            {
                array.Add(app.ToJson());
            }
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, array.Stringify());
            }
            catch (Exception ex)
            {
                Log.Error("Saving apps.json failed", ex);
            }
        }
    }
}
