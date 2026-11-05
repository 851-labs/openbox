/* menu.js: X opens a video's options, as in YouTube's own console and TV apps.
 *
 * YouTube's TV app opens a tile's menu (Save to Watch later, Save to playlist, Not interested...)
 * when OK is held down for half a second, on devices that say they can (supports_long_press,
 * which YouTube sets for the Xbox). X holds OK down on a video, playlist or channel tile that has
 * a menu; anywhere else (the player, say) X stays play/pause (remote.js).
 */
(function () {
    if (!isTop) return;

    /* Longer than YouTube's 500 ms, which starts at its own keydown handler. */
    var HOLD_MS = 700;

    /* What OK does is up to the nearest of YouTube's components around the focus that answers it,
       as YouTube looks it up: a tile with a menu answers a long press too. */
    function hasMenu(el) {
        for (; el; el = el.parentElement) {
            var component = el.__instance;
            if (!component) continue;
            var props = component.props || {};
            if (props.onLongPress || component.onLongPress) return true;
            if (props.onSelect || component.onSelect) return false;
        }
        return false;
    }

    remoteHooks.push(function (msg, press) {
        if (msg.action !== 'x' || msg.repeat || !hasMenu(document.activeElement)) return false;
        press('select', HOLD_MS);
        return true;
    });
})();
