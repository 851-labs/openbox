using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using OpenBox.Diagnostics;
using OpenBox.Web;
using Windows.Data.Json;

namespace OpenBox.AdBlock
{
    /// <summary>
    /// The ad blocker every web app gets (YouTube has its own, from TubeBox). It works the way
    /// uBlock Origin does, with its default lists (<see cref="AdFilters"/>): requests to ad and
    /// tracking servers are refused before they leave the console, and Inject/cosmetic.js hides
    /// the ad slots left on the page.
    /// </summary>
    internal sealed class AdBlocker : IDisposable
    {
        private readonly CoreWebView2 _core;
        private readonly AdFilters _filters;
        private string _pageHost;
        private int _blocked;

        private AdBlocker(CoreWebView2 core, AdFilters filters, string startUrl)
        {
            _core = core;
            _filters = filters;
            _pageHost = HostOf(startUrl);
            // Every request is checked, the way an ad blocker sees them all.
            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnWebResourceRequested;
            _core.NavigationStarting += OnNavigationStarting;
            _core.WebMessageReceived += OnWebMessageReceived;
        }

        /// <summary>Null if there are no filters to block with.</summary>
        public static async Task<AdBlocker> AttachAsync(CoreWebView2 core, string startUrl)
        {
            var filters = await AdFilters.GetAsync();
            if (filters == null)
            {
                return null;
            }
            var blocker = new AdBlocker(core, filters, startUrl);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(await WebAppPage.ReadScriptAsync("AdBlock/Inject/cosmetic.js"));
            return blocker;
        }

        public void Dispose()
        {
            _core.WebResourceRequested -= OnWebResourceRequested;
            _core.NavigationStarting -= OnNavigationStarting;
            _core.WebMessageReceived -= OnWebMessageReceived;
            try
            {
                _core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            }
            catch (Exception)
            {
                // The web view is already gone.
            }
            Log.Write("Ad blocker: " + _blocked + " requests blocked");
        }

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _pageHost = HostOf(e.Uri) ?? _pageHost;
        }

        private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return;
            }
            if (_filters.ShouldBlock(uri, _pageHost))
            {
                e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
                _blocked++;
            }
        }

        // cosmetic.js asks for the page's selectors as it starts.
        private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonObject message;
            if (!JsonObject.TryParse(e.WebMessageAsJson, out message))
            {
                return;
            }
            var type = message.GetNamedValue("type", JsonValue.CreateNullValue());
            if (type.ValueType != JsonValueType.String || type.GetString() != "adblock.cosmetic")
            {
                return;
            }
            var host = HostOf(e.Source);
            if (host == null)
            {
                return;
            }
            var reply = new JsonObject();
            reply.SetNamedValue("type", JsonValue.CreateStringValue("adblock.cosmetic"));
            reply.SetNamedValue("host", JsonValue.CreateStringValue(host));
            reply.SetNamedValue("selectors", JsonValue.CreateStringValue(_filters.SelectorsFor(host)));
            try
            {
                sender.PostWebMessageAsJson(reply.Stringify());
            }
            catch (Exception ex)
            {
                Log.Write("Sending cosmetic filters failed: " + ex.Message);
            }
        }

        private static string HostOf(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.Host : null;
        }
    }
}
