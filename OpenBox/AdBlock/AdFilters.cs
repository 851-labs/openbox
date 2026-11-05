using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenBox.Diagnostics;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace OpenBox.AdBlock
{
    /// <summary>
    /// The ad blocker's filters, compiled from uBlock Origin's default lists by
    /// tools/build_adblock.py (which says what each part is). The app comes with the copy CI built
    /// it with; a fresh one is published daily on the store branch, downloaded once a day and kept
    /// in LocalState.
    /// </summary>
    internal sealed class AdFilters
    {
        private const string UpdateUrl = "https://raw.githubusercontent.com/851-labs/openbox/store/adblock.json";
        private const string CacheFileName = "adblock.json";
        private const string LastCheckKey = "adblockLastCheck";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private static readonly Regex VersionPattern = new Regex("^\\{\"version\":(\\d+)");
        private static readonly string[] SecondLevelLabels = { "co", "com", "net", "org", "gov", "edu", "ac", "or", "ne", "go", "gob", "mil" };

        private static Task<AdFilters> _current;

        private HashSet<string> _block, _block3p, _allow, _noCosmetic, _noGenericCosmetic;
        private Dictionary<string, string[]> _blockPaths, _hideFor;
        private Dictionary<string, HashSet<string>> _allowOn, _unhideFor;
        private string _hideText;
        private string[] _hide;

        public long Version { get; private set; }
        public string Updated { get; private set; }

        /// <summary>The filters, loaded once (they're large: start early with <see cref="Preload"/>). Null if there are none.</summary>
        public static Task<AdFilters> GetAsync()
        {
            if (_current == null)
            {
                _current = LoadAsync();
            }
            return _current;
        }

        /// <summary>Starts loading the filters, and checks for newer ones once a day. Call on the UI thread.</summary>
        public static async void Preload()
        {
            var current = await GetAsync();
            var settings = ApplicationData.Current.LocalSettings.Values;
            object last;
            if (settings.TryGetValue(LastCheckKey, out last) && last is long &&
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds((long)last) < CheckInterval)
            {
                return;
            }
            settings[LastCheckKey] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                var filter = new HttpBaseProtocolFilter();
                filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
                filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                using (var client = new HttpClient(filter))
                using (var response = await client.GetAsync(new Uri(UpdateUrl)))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Write("Ad filter check: the server answered " + (int)response.StatusCode);
                        return;
                    }
                    var text = await response.Content.ReadAsStringAsync();
                    if (ReadVersion(text) <= (current != null ? current.Version : 0))
                    {
                        Log.Write("Ad filters are up to date");
                        return;
                    }
                    var updated = await Task.Run(() => Parse(text));
                    if (updated == null)
                    {
                        return;
                    }
                    var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(CacheFileName, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(file, text);
                    // Apps opened from now on use them.
                    _current = Task.FromResult(updated);
                    Log.Write("Ad filters updated to " + updated.Updated);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Ad filter check failed: " + ex.Message);
            }
        }

        private static async Task<AdFilters> LoadAsync()
        {
            var bundled = await ReadTextAsync(() => StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///AdBlock/adblock.json")).AsTask());
            var cachePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, CacheFileName);
            var cached = File.Exists(cachePath) ? await ReadTextAsync(() => StorageFile.GetFileFromPathAsync(cachePath).AsTask()) : null;
            var text = ReadVersion(cached) > ReadVersion(bundled) ? cached : bundled;
            if (text == null)
            {
                Log.Write("No ad filters; web apps open without the ad blocker");
                return null;
            }
            var started = DateTime.UtcNow;
            var filters = await Task.Run(() => Parse(text));
            if (filters != null)
            {
                Log.Write(string.Format("Ad filters from {0} ({1}) loaded in {2:0} ms: {3} blocked hosts, {4} generic cosmetic filters",
                    filters.Updated, text == cached ? "downloaded" : "built in", (DateTime.UtcNow - started).TotalMilliseconds,
                    filters._block.Count + filters._block3p.Count, filters._hide.Length));
            }
            return filters;
        }

        private static async Task<string> ReadTextAsync(Func<Task<StorageFile>> open)
        {
            try
            {
                return await FileIO.ReadTextAsync(await open());
            }
            catch (Exception ex)
            {
                Log.Write("Reading ad filters failed: " + ex.Message);
                return null;
            }
        }

        // The file starts with its version, so which of two copies is newer can be told without parsing them.
        private static long ReadVersion(string text)
        {
            var match = text != null ? VersionPattern.Match(text.Substring(0, Math.Min(text.Length, 64))) : null;
            long version;
            return match != null && match.Success && long.TryParse(match.Groups[1].Value, out version) ? version : 0;
        }

        private static AdFilters Parse(string text)
        {
            try
            {
                return ParseJson(JsonObject.Parse(text));
            }
            catch (Exception ex)
            {
                Log.Error("The ad filters aren't valid", ex);
                return null;
            }
        }

        private static AdFilters ParseJson(JsonObject json)
        {
            return new AdFilters
            {
                Version = (long)json.GetNamedNumber("version", 0),
                Updated = json.GetNamedString("updated", ""),
                _block = new HashSet<string>(Lines(json, "block")),
                _block3p = new HashSet<string>(Lines(json, "block3p")),
                _allow = new HashSet<string>(Lines(json, "allow")),
                _noCosmetic = new HashSet<string>(Lines(json, "noCosmetic")),
                _noGenericCosmetic = new HashSet<string>(Lines(json, "noGenericCosmetic")),
                _blockPaths = Table(json, "blockPaths", lines => lines),
                _hideFor = Table(json, "hideFor", lines => lines),
                _allowOn = Table(json, "allowOn", lines => new HashSet<string>(lines)),
                _unhideFor = Table(json, "unhideFor", lines => new HashSet<string>(lines)),
                _hideText = json.GetNamedString("hide", ""),
                _hide = Lines(json, "hide"),
            };
        }

        private static string[] Lines(JsonObject json, string name)
        {
            return Split(json.GetNamedString(name, ""));
        }

        private static string[] Split(string text)
        {
            return text.Length == 0 ? new string[0] : text.Split('\n');
        }

        private static Dictionary<string, T> Table<T>(JsonObject json, string name, Func<string[], T> make)
        {
            var result = new Dictionary<string, T>();
            foreach (var entry in json.GetNamedObject(name, new JsonObject()))
            {
                if (entry.Value.ValueType == JsonValueType.String)
                {
                    result[entry.Key] = make(Split(entry.Value.GetString()));
                }
            }
            return result;
        }

        /// <summary>Whether a request from a page at <paramref name="pageHost"/> should be refused.</summary>
        public bool ShouldBlock(Uri request, string pageHost)
        {
            var host = request.Host;
            // The app's own site is never blocked: these are host filters, and blocking a host the
            // page itself is on would break the app rather than its ads.
            if (pageHost != null && BaseDomain(host) == BaseDomain(pageHost))
            {
                return false;
            }
            if (Matches(_allow, host))
            {
                return false;
            }
            if (pageHost != null)
            {
                for (var site = pageHost; site != null; site = Parent(site))
                {
                    HashSet<string> allowed;
                    if (_allowOn.TryGetValue(site, out allowed) && Matches(allowed, host))
                    {
                        return false;
                    }
                }
            }
            // Every request reaching here is third-party.
            if (Matches(_block, host) || Matches(_block3p, host))
            {
                return true;
            }
            for (var h = host; h != null; h = Parent(h))
            {
                string[] paths;
                if (_blockPaths.TryGetValue(h, out paths))
                {
                    var path = request.PathAndQuery;
                    if (paths.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>The selectors to hide on a page at <paramref name="host"/>, a line each.</summary>
        public string SelectorsFor(string host)
        {
            if (Matches(_noCosmetic, host))
            {
                return string.Empty;
            }
            HashSet<string> unhide = null;
            var specific = new List<string>();
            for (var site = host; site != null; site = Parent(site))
            {
                HashSet<string> excepted;
                if (_unhideFor.TryGetValue(site, out excepted))
                {
                    unhide = unhide ?? new HashSet<string>();
                    unhide.UnionWith(excepted);
                }
                string[] selectors;
                if (_hideFor.TryGetValue(site, out selectors))
                {
                    specific.AddRange(selectors);
                }
            }
            if (unhide != null)
            {
                specific.RemoveAll(unhide.Contains);
            }
            var generic = string.Empty;
            if (!Matches(_noGenericCosmetic, host))
            {
                generic = unhide == null ? _hideText : string.Join("\n", _hide.Where(s => !unhide.Contains(s)));
            }
            if (specific.Count == 0)
            {
                return generic;
            }
            return generic.Length == 0 ? string.Join("\n", specific) : string.Join("\n", specific) + "\n" + generic;
        }

        // Whether the host, or a domain it's under, is in the set.
        private static bool Matches(HashSet<string> set, string host)
        {
            for (var h = host; h != null; h = Parent(h))
            {
                if (set.Contains(h))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Parent(string host)
        {
            var dot = host.IndexOf('.');
            return dot >= 0 ? host.Substring(dot + 1) : null;
        }

        /// <summary>
        /// The registrable domain, near enough: the last two labels, or three under the likes of
        /// co.uk. A full public suffix list isn't worth carrying for this.
        /// </summary>
        private static string BaseDomain(string host)
        {
            var labels = host.Split('.');
            if (labels.Length <= 2 || char.IsDigit(host[host.Length - 1]))
            {
                return host;
            }
            var count = labels[labels.Length - 1].Length == 2 && Array.IndexOf(SecondLevelLabels, labels[labels.Length - 2]) >= 0 ? 3 : 2;
            return string.Join(".", labels, labels.Length - count, count);
        }
    }
}
