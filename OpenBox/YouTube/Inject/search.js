/* search.js: YouTube's search, typed on the console's keyboard.
 *
 * YouTube's TV app has a keyboard of its own on its search page, and no text field. On the
 * console, when its keyboard takes the focus, a real text field goes over YouTube's search box
 * instead, and focusing it brings up the console's keyboard (WebAppPage hands what's typed on it
 * to the field). Enter searches for what was typed, the way YouTube's own address for a search
 * does (#/search?q=), which takes any text. B closes the console's keyboard and leaves YouTube's,
 * where A brings the console's back.
 */
(function () {
    if (!isTop || !webview || !config.xbox) return;

    var field = null;
    /* Where YouTube's focus goes once the field is gone: its key that had it, or where YouTube
       moved it since. */
    var returnTo = null;
    /* Closed with B: YouTube's keyboard doesn't bring it back until the focus has left it. */
    var dismissed = false;

    function inKeyboard(el) {
        return !!(el && el.closest && el.closest('ytlr-search-keyboard'));
    }

    function searchBox() {
        return document.querySelector('ytlr-search-text-box');
    }

    function currentQuery(box) {
        try {
            return box.__instance.props.searchQuery.userInput || '';
        } catch (e) {
            return '';
        }
    }

    var style = document.createElement('style');
    style.textContent =
        '#openbox-search { position: fixed; z-index: 2147483647; box-sizing: border-box; margin: 0;' +
        ' padding: 0 24px; border: 0; outline: 3px solid #fff; color: #fff; background: #3f3f3f;' +
        ' font: 700 24px Roboto, Arial, sans-serif; caret-color: #fff; pointer-events: none; }' +
        '#openbox-search::placeholder { color: #aaa; }' +
        '#openbox-search::-webkit-search-cancel-button { display: none; }';

    function open() {
        var box = searchBox();
        if (field || !box) return;
        var rect = box.getBoundingClientRect();
        if (!rect.width) return;
        if (!style.isConnected) (document.head || document.documentElement).appendChild(style);
        returnTo = inKeyboard(document.activeElement) ? document.activeElement : null;
        dismissed = false;
        field = document.createElement('input');
        field.id = 'openbox-search';
        field.type = 'search';
        field.enterKeyHint = 'search';
        field.autocomplete = 'off';
        field.spellcheck = false;
        field.placeholder = 'Search YouTube';
        field.value = currentQuery(box);
        field.style.left = rect.left + 'px';
        field.style.top = rect.top + 'px';
        field.style.width = Math.max(rect.width, 600) + 'px';
        field.style.height = rect.height + 'px';
        field.style.borderRadius = rect.height / 2 + 'px';
        // The field's keys are its own: YouTube's keyboard handling, further up, cancels some
        // (Backspace is its back).
        ['keydown', 'keypress', 'keyup'].forEach(function (type) {
            field.addEventListener(type, function (e) {
                e.stopPropagation();
                if (type === 'keydown' && e.key === 'Enter') {
                    e.preventDefault();
                    submit();
                }
            });
        });
        document.body.appendChild(field);
        field.focus();
        field.setSelectionRange(field.value.length, field.value.length);
        log('Typing a search');
    }

    function close() {
        if (!field) return;
        var el = field;
        field = null;
        el.remove();
        post('app.keyboard.hide', {});
    }

    function restore() {
        if (returnTo && returnTo.isConnected) returnTo.focus();
        returnTo = null;
    }

    function submit() {
        var query = field.value.trim();
        close();
        var hash = '#/search?q=' + encodeURIComponent(query);
        if (!query || location.hash === hash) {
            dismissed = true;
            restore();
            return;
        }
        returnTo = null;
        log('Searching');
        location.hash = hash;
    }

    function cancel() {
        dismissed = true;
        close();
        restore();
    }

    /* YouTube puts the browser's focus back on its own key each time it draws the page (right
       after the search page opens, say), which would take it off the field and the console's
       keyboard down. While the field is up, where YouTube wants the focus is only noted, for when
       the field goes. */
    wrapMethod(HTMLElement.prototype, 'focus', function (focus) {
        return function () {
            if (field && this !== field) {
                returnTo = this;
                return;
            }
            return focus.apply(this, arguments);
        };
    });

    /* YouTube's keyboard taking the focus (opening Search, or Up from the results) brings up the
       console's instead. */
    document.addEventListener('focusin', function (e) {
        var target = e.target;
        if (target === field) return;
        if (!inKeyboard(target)) {
            dismissed = false;
        } else if (!field && !dismissed) {
            setTimeout(function () {
                if (inKeyboard(document.activeElement)) open();
            }, 0);
        }
    }, true);

    webview.addEventListener('message', function (event) {
        var msg = event.data;
        // The console's keyboard closed (its B) without Enter. WebAppPage sends this after the
        // keys typed before it, so an Enter that closed it has already searched.
        if (msg && msg.type === 'keyboard' && !msg.visible && field) cancel();
    });

    remoteHooks.push(function (msg) {
        if (field) {
            // The controller only reaches the page while the console's keyboard is down: the
            // field is up without it. A tries again, B leaves the field, the rest wait.
            if (msg.action === 'select') {
                field.blur();
                field.focus();
            } else if (msg.action === 'back') {
                cancel();
            }
            return true;
        }
        // A on YouTube's keyboard brings the console's back rather than typing a letter.
        if (msg.action !== 'select' || !inKeyboard(document.activeElement)) return false;
        open();
        return true;
    });
})();
