using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace OpenBox.Web
{
    /// <summary>
    /// What a built-in web app adds to <see cref="WebAppPage"/>: YouTube's ad blocking, for one.
    /// Store apps run without one.
    /// </summary>
    internal interface IWebAppExtension : IDisposable
    {
        /// <summary>The user agent to use instead of the app's own, or null.</summary>
        string UserAgent { get; }

        /// <summary>Called once the web view is ready, before the first page loads.</summary>
        Task AttachAsync(CoreWebView2 core);

        /// <summary>
        /// Script to run in every page, inside prelude.js's function (so it can use post, log,
        /// wrapMethod and config). Empty for none.
        /// </summary>
        Task<string> GetPageScriptAsync();

        /// <summary>Whether the app may leave for this address. Others are cancelled.</summary>
        bool AllowsNavigation(Uri uri);
    }
}
