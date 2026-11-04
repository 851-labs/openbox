using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using OpenBox.Web;
using Windows.Data.Json;

namespace OpenBox.YouTube
{
    /// <summary>
    /// YouTube's own TV app (youtube.com/tv, the one smart TVs and consoles run), without the ads.
    /// From TubeBox (github.com/sharktie/tubebox-uwp): its user agent, ad filters, request blocking
    /// and the page scripts in Inject/.
    /// </summary>
    internal sealed class YouTubeExtension : IWebAppExtension
    {
        /// <summary>
        /// env_forceFullAnimation turns on the slides and fades the TV app leaves out for the device
        /// openbox says it is.
        /// </summary>
        public const string Url = "https://www.youtube.com/tv?env_forceFullAnimation=true";

        /// <summary>
        /// YouTube only serves its TV app to TVs and consoles. This is the Xbox's YouTube app: on some
        /// videos YouTube holds the picture back for as long as a blocked ad when it thinks it is
        /// talking to other devices, but not to an Xbox. The filter list can replace it.
        /// </summary>
        private const string TvUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; Xbox; Xbox Series X) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/48.0.2564.82 Safari/537.36 Edge/20.02";

        private static readonly string[] Scripts = { "adblock.js", "upgrade.js" };

        private FilterList _filters;
        private RequestBlocker _blocker;

        public string UserAgent
        {
            get { return _filters?.UserAgent ?? TvUserAgent; }
        }

        public bool HandlesInput
        {
            get { return false; }
        }

        public async Task AttachAsync(CoreWebView2 core)
        {
            _filters = await FilterList.LoadAsync();
            _blocker = new RequestBlocker(core, _filters.BlockUrls);
            // Newer filters, if any, take effect the next time YouTube opens.
            var ignored = FilterList.UpdateAsync(_filters, false);
        }

        public async Task<string> GetPageScriptAsync()
        {
            var config = new JsonObject();
            config.SetNamedValue("blockAds", JsonValue.CreateBooleanValue(true));
            config.SetNamedValue("filters", _filters.ToPageConfig());

            // The scripts read config.blockAds and config.filters, as in TubeBox: they get the page's
            // config with those added.
            var script = new StringBuilder();
            script.Append("if (/(^|\\.)youtube\\.com$/.test(location.hostname)) (function (config) {\n");
            foreach (var name in Scripts)
            {
                script.Append(await WebAppPage.ReadScriptAsync("YouTube/Inject/" + name)).Append('\n');
            }
            script.Append("})(Object.assign({}, config, ").Append(config.Stringify()).Append("));\n");
            return script.ToString();
        }

        public bool AllowsNavigation(Uri uri)
        {
            // Only YouTube runs in here. Sign-in happens on a phone, at yt.be/activate.
            return uri.Scheme == Uri.UriSchemeHttps &&
                (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            _blocker?.Dispose();
        }
    }
}
