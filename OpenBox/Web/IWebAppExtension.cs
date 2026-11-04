using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace OpenBox.Web
{
    /// <summary>
    /// What a built-in web app adds to <see cref="WebAppPage"/>: YouTube's ad blocking, or X's
    /// TV layout.
    /// Store apps run without one.
    /// </summary>
    internal interface IWebAppExtension : IDisposable
    {
        /// <summary>The user agent to use instead of the app's own, or null.</summary>
        string UserAgent { get; }

        /// <summary>
        /// True if the page script reads the controller's input messages itself (X's does), so
        /// remote.js doesn't turn them into TV remote keys. It can then ask for the console's mouse
        /// pointer on pages it can't drive: see the app.pointer message in WebAppPage.
        /// </summary>
        bool HandlesInput { get; }

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
