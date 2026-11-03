using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenBox.Apps;
using OpenBox.Diagnostics;
using Windows.Data.Json;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace OpenBox.Store
{
    /// <summary>
    /// The store's app list. CI builds it from the apps/ folder of the repository
    /// (tools/build_store.py) and publishes it, with the apps' icons, on the repository's store branch.
    /// </summary>
    internal static class StoreIndex
    {
        public const string IndexUrl = "https://raw.githubusercontent.com/851-labs/openbox/store/index.json";

        /// <summary>Brings installed apps up to date with the store (a new address, say), in the background.</summary>
        public static async void RefreshInstalled()
        {
            try
            {
                await AppLibrary.UpdateFromStoreAsync(await LoadAsync());
            }
            catch (Exception ex)
            {
                Log.Write("Checking the store for app updates failed: " + ex.Message);
            }
        }

        /// <summary>The store's apps, or an exception saying why they couldn't be loaded.</summary>
        public static async Task<List<AppEntry>> LoadAsync()
        {
            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
            using (var client = new HttpClient(filter))
            using (var response = await client.GetAsync(new Uri(IndexUrl)))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception("The store answered " + (int)response.StatusCode + ".");
                }
                JsonObject json;
                if (!JsonObject.TryParse(await response.Content.ReadAsStringAsync(), out json))
                {
                    throw new Exception("The store's app list isn't valid.");
                }
                var apps = new List<AppEntry>();
                foreach (var value in json.GetNamedArray("apps", new JsonArray()))
                {
                    var app = value.ValueType == JsonValueType.Object ? AppEntry.FromJson(value.GetObject()) : null;
                    if (app != null && !BuiltInApps.IsReservedId(app.Id))
                    {
                        apps.Add(app);
                    }
                }
                Log.Write("Store: " + apps.Count + " apps");
                return apps;
            }
        }
    }
}
