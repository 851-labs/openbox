/* cosmetic.js: the ad blocker's cosmetic filters, as uBlock Origin applies them.
 *
 * Runs at the start of every page. It asks the app (AdBlock/AdBlocker.cs) for the selectors to
 * hide on this site, its own filters and the generic ones, and hides them with a stylesheet.
 * One selector this browser can't parse would throw out every rule around it, so they go in in
 * groups and a group that fails goes in again one selector at a time.
 */
(function () {
    'use strict';
    var webview = window.chrome && window.chrome.webview;
    if (!webview || window.top !== window || !/^https?:$/.test(location.protocol)) return;

    var GROUP = 50;
    var host = location.hostname;
    var selectors = null;
    var style = null;

    function insert(sheet, group) {
        try {
            sheet.insertRule(group.join(',\n') + '{display:none!important}', sheet.cssRules.length);
        } catch (e) {
            if (group.length > 1) group.forEach(function (selector) { insert(sheet, [selector]); });
        }
    }

    function apply() {
        var root = document.head || document.documentElement;
        if (!selectors || !selectors.length || !root || (style && style.isConnected)) return;
        style = document.createElement('style');
        style.setAttribute('data-openbox-adblock', '');
        root.appendChild(style);
        for (var i = 0; i < selectors.length; i += GROUP) insert(style.sheet, selectors.slice(i, i + GROUP));
    }

    webview.addEventListener('message', function (event) {
        var msg = event.data;
        if (!msg || msg.type !== 'adblock.cosmetic' || msg.host !== host || selectors) return;
        selectors = msg.selectors ? msg.selectors.split('\n') : [];
        apply();
        // Pages that rebuild their <head> take the stylesheet with them: put it back.
        new MutationObserver(apply).observe(document.documentElement, { childList: true });
        if (document.head) new MutationObserver(apply).observe(document.head, { childList: true });
    });
    webview.postMessage({ type: 'adblock.cosmetic', host: host });
})();
