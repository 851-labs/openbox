using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using OpenBox.Web;
using Windows.Data.Json;

namespace OpenBox.X
{
    /// <summary>
    /// X (x.com), driven with the D-pad. X's own TV app is gone (x.com/i/tv only says it's no
    /// longer supported), so this is the ordinary site with a TV layer on top: Inject/tv.css lays
    /// it out for the TV and Inject/tv.js moves a focus from post to post and plays videos full
    /// screen. Sign-in is X's own, with the console's mouse pointer and keyboard.
    ///
    /// X gets no ad blocker: its promoted posts are left as they are (see AdFilters too).
    /// </summary>
    internal sealed class XExtension : IWebAppExtension
    {
        public const string Url = "https://x.com/home";

        /// <summary>WebView2's own: X serves its ordinary site to it.</summary>
        public string UserAgent
        {
            get { return null; }
        }

        public bool HandlesInput
        {
            get { return true; }
        }

        public Task AttachAsync(CoreWebView2 core)
        {
            return Task.CompletedTask;
        }

        public async Task<string> GetPageScriptAsync()
        {
            // tv.js adds the stylesheet itself, as soon as the page has somewhere to put it.
            var script = new StringBuilder();
            script.Append("var css = ")
                .Append(JsonValue.CreateStringValue(await WebAppPage.ReadScriptAsync("X/Inject/tv.css")).Stringify())
                .Append(";\n");
            script.Append(await WebAppPage.ReadScriptAsync("X/Inject/tv.js")).Append('\n');
            return script.ToString();
        }

        public bool AllowsNavigation(Uri uri)
        {
            // Only X runs in here: links out of a post (t.co and the sites behind it) stay closed.
            return uri.Scheme == Uri.UriSchemeHttps && (IsHost(uri, "x.com") || IsHost(uri, "twitter.com"));
        }

        private static bool IsHost(Uri uri, string domain)
        {
            return uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
        }
    }
}
