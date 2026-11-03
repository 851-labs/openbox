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
    /// (tools/build_store.py) and publishes it on the repository's GitHub Pages site.
    /// </summary>
    internal static class StoreIndex
    {
        public const string IndexUrl = "https://851-labs.github.io/openbox/index.json";

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
