/* prelude.js: shared by the scripts after it. From TubeBox (sharktie/tubebox-uwp).
 *
 * WebAppPage.xaml.cs joins this file, the app's own scripts (YouTube's, say) and, for apps driven
 * like a TV remote, remote.js into one script and runs it at the start of every page, before the
 * page's own code:
 *
 *   (function (config) { prelude.js, ...the app's scripts..., remote.js })({ xbox, ... });
 */
var webview = window.chrome && window.chrome.webview;
var isTop = window.top === window;
var hasOwn = Object.prototype.hasOwnProperty;

function post(type, payload) {
    if (!webview || !isTop) return;
    var msg = { type: type };
    for (var k in payload) if (hasOwn.call(payload, k)) msg[k] = payload[k];
    try { webview.postMessage(msg); } catch (e) {}
}

/* An app's own script can take a controller action before remote.js turns it into a remote key
   (YouTube's X, say): remote.js calls each of these with the input message and a press(action,
   holdMs) that presses another remote key, held down for holdMs if given. One that returns true
   has handled the action. */
var remoteHooks = [];

function log(message) {
    post('app.log', { message: String(message) });
}

/* Replaces obj[name] with a wrapper that still reads as native code, so the page can't tell. */
function wrapMethod(obj, name, make) {
    var original = obj[name];
    if (typeof original !== 'function') return;
    var wrapper = make(original);
    var text = 'function ' + name + '() { [native code] }';
    try {
        Object.defineProperty(wrapper, 'toString', { value: function () { return text; }, configurable: true });
        Object.defineProperty(wrapper, 'name', { value: name, configurable: true });
    } catch (e) {}
    try {
        Object.defineProperty(obj, name, { value: wrapper, writable: true, configurable: true, enumerable: false });
    } catch (e) {
        obj[name] = wrapper;
    }
}

/* An app closing itself (YouTube does when you back out of its start screen) goes back to the
   homescreen. The browser would ignore window.close() for a page it didn't open by script. */
if (isTop) {
    wrapMethod(window, 'close', function () {
        return function () { post('app.exit', {}); };
    });
}
