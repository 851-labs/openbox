/* tv.js: x.com, driven with a controller. X's TV app is gone (x.com/i/tv only says so), so this is
 * a TV layer on top of the ordinary site, with tv.css (in `css`, from X/XExtension.cs):
 *
 *   up / down     the next or previous post (a tall one scrolls first)
 *   A             the post's video, full screen with sound; a post without one opens
 *   X             play / pause                  LT / RT    back / forward 10 seconds
 *   LB / RB       For you, Following (and pinned lists), Videos
 *   Y             back to the top
 *   B             closes the video, then goes back a page, then leaves X
 *
 * The app posts the controller as {type: 'input', action, repeat} (Web/ControllerBridge.cs), as it
 * does for remote.js, which X doesn't get. Sign-in (/i/flow/..., and the pages around it) is a form,
 * not a timeline: there the D-pad moves a focus ring between its fields and buttons, A on a field
 * opens the console's keyboard and A on a button presses it, and B leaves the field, then goes
 * back a step.
 *
 * X recycles the timeline's elements as it scrolls, so the focus follows a post's id, not an
 * element, and a MutationObserver puts it back on the right post whenever X re-renders.
 */
if (isTop && webview && /(^|\.)(x|twitter)\.com$/.test(location.hostname)) (function () {
    var POSTS = '[data-testid="primaryColumn"] article[data-testid="tweet"]';
    var VIDEOS_URL = '/search?q=' + encodeURIComponent('filter:native_video min_faves:2000 lang:en') +
        '&src=typed_query&f=top';
    /* Forms rather than timelines: sign-in, sign-up and the signed-out start page. */
    var FORM_PAGES = /^\/(?:$|i\/flow\/|i\/jf\/|login|logout|account\/|i\/oauth|i\/bouncer)/;
    var FIELDS = 'input:not([type="hidden"]):not([disabled]), textarea, select, button:not([disabled]), ' +
        'a[href], [role="button"]:not([aria-disabled="true"]), [role="link"], [role="checkbox"], ' +
        '[role="radio"], [role="switch"], [role="option"], [role="tab"], [contenteditable="true"]';
    var TEXT_TYPES = /^(?:text|email|password|tel|number|search|url)$/;
    /* Google's and Apple's sign-in need a pop-up window, which the app doesn't have: the D-pad
       skips their buttons (Google's is an iframe of its own). */
    var POPUP_SIGN_IN = /\b(?:Google|Apple)\b/;
    var SEEK_SECONDS = 10;
    var BAR_HEIGHT = 64;
    var KEY_ACTION = {
        ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'select',
        Escape: 'back', BrowserBack: 'back', ' ': 'x', PageUp: 'lb', PageDown: 'rb', Home: 'y'
    };

    var on = false;
    var formEl = null;
    var focusId = null;
    var focusEl = null;
    var focusByUrl = {};
    var url = location.href;
    var feedNames = ['For you', 'Following'];
    var pendingTab = -1;
    var bar = null;
    var barHtml = '';
    var viewer = null;
    var scheduled = false;
    var zoom = 1;

    /* -- the page -------------------------------------------------------------------------------- */

    /* The script runs before the page has an <html> element: this is called once it has, and
       again whenever X changes the page. */
    function dressPage() {
        var root = document.documentElement;
        if (!document.getElementById('openbox-x-style')) {
            var style = document.createElement('style');
            style.id = 'openbox-x-style';
            style.textContent = css;
            (document.head || root).appendChild(style);
        }
        zoom = Math.round(window.innerHeight / 720 * 1000) / 1000;
        root.style.setProperty('--openbox-zoom', String(zoom));
        root.style.setProperty('--openbox-unzoom', String(1 / zoom));
        root.toggleAttribute('data-openbox-xbox', !!config.xbox);
        root.toggleAttribute('data-openbox-tv', on);
    }

    function isHome() {
        return location.pathname === '/home';
    }

    function isVideos() {
        return location.pathname === '/search' && location.search === VIDEOS_URL.slice('/search'.length);
    }

    /* The timeline layer is on everywhere but sign-in, which is driven as a form. */
    function updateMode() {
        var wanted = !FORM_PAGES.test(location.pathname);
        if (wanted !== on) {
            on = wanted;
            log(on ? 'Timeline layer on' : 'Form layer on for ' + location.pathname);
        }
    }

    function posts() {
        var list = Array.prototype.slice.call(document.querySelectorAll(POSTS)).filter(function (el) {
            return el.getClientRects().length > 0;
        });
        var tops = list.map(function (el) { return el.getBoundingClientRect().top; });
        var order = list.map(function (el, i) { return i; }).sort(function (a, b) { return tops[a] - tops[b]; });
        return order.map(function (i) { return list[i]; });
    }

    /* The post's id, from the link on its time. Promoted posts have none: their author and text do. */
    function idOf(article) {
        var time = article.querySelector('a[href*="/status/"] time');
        var link = time ? time.closest('a') : article.querySelector('a[href*="/status/"]');
        var match = link && /\/status\/(\d+)/.exec(link.getAttribute('href'));
        if (match) return match[1];
        var name = article.querySelector('[data-testid="User-Name"]');
        var text = article.querySelector('[data-testid="tweetText"]');
        return 'x:' + (name ? name.textContent : '') + ':' + (text ? text.textContent.slice(0, 60) : '');
    }

    function topInset() {
        return BAR_HEIGHT * zoom + 8;
    }

    function setFocus(article) {
        if (focusEl && focusEl !== article) focusEl.removeAttribute('data-openbox-focus');
        focusEl = article;
        focusId = article ? idOf(article) : null;
        focusByUrl[location.href] = focusId;
        if (article) article.setAttribute('data-openbox-focus', '');
    }

    /* After X re-renders: the focus back on its post, wherever that is now, and off the rest. */
    function refocus() {
        var marked = document.querySelectorAll('article[data-openbox-focus]');
        for (var i = 0; i < marked.length; i++) {
            if (marked[i] !== focusEl || idOf(marked[i]) !== focusId) marked[i].removeAttribute('data-openbox-focus');
        }
        if (focusEl && (!focusEl.isConnected || idOf(focusEl) !== focusId)) focusEl = null;
        if (!focusEl && focusId) {
            var list = posts();
            for (var j = 0; j < list.length; j++) {
                if (idOf(list[j]) === focusId) {
                    focusEl = list[j];
                    break;
                }
            }
        }
        if (!focusEl && !focusId) {
            // Nothing focused yet: the first post on screen.
            var first = posts().filter(function (el) { return el.getBoundingClientRect().bottom > topInset(); })[0];
            if (first) setFocus(first);
        }
        if (focusEl && !focusEl.hasAttribute('data-openbox-focus')) focusEl.setAttribute('data-openbox-focus', '');
    }

    function scrollBy(delta, instant) {
        if (Math.abs(delta) < 2) return;
        window.scrollBy({ top: delta, behavior: instant ? 'auto' : 'smooth' });
    }

    function reveal(article, instant) {
        scrollBy(article.getBoundingClientRect().top - topInset(), instant);
    }

    function move(step, repeat) {
        var list = posts();
        if (!list.length) return;
        var inset = topInset();
        var height = window.innerHeight;
        var index = focusEl ? list.indexOf(focusEl) : -1;
        if (index < 0) {
            // The focused post has scrolled away and X has let go of it: start from the screen.
            var onScreen = list.filter(function (el) {
                var rect = el.getBoundingClientRect();
                return rect.bottom > inset && rect.top < height;
            });
            var start = onScreen[0] || list[0];
            setFocus(start);
            reveal(start, repeat);
            return;
        }
        var rect = focusEl.getBoundingClientRect();
        // A post taller than the screen scrolls through before the next one gets the focus.
        if (step > 0 && rect.bottom > height + 4) {
            scrollBy(Math.min(rect.bottom - height + 24, height * 0.6), repeat);
            return;
        }
        if (step < 0 && rect.top < inset - 4) {
            scrollBy(Math.max(rect.top - inset, -height * 0.6), repeat);
            return;
        }
        var next = list[index + step];
        if (!next) {
            // The end of what's loaded: scrolling on makes X load more.
            if (step > 0) scrollBy(height * 0.6, repeat);
            else scrollBy(-window.scrollY, repeat);
            return;
        }
        setFocus(next);
        var nextRect = next.getBoundingClientRect();
        if (step < 0 && nextRect.height > height - inset) {
            // Going up onto a tall post: show its end, the part next to where the focus was.
            scrollBy(nextRect.bottom - height + 24, repeat);
        } else {
            reveal(next, repeat);
        }
    }

    function toTop() {
        window.scrollTo({ top: 0, behavior: 'auto' });
        focusId = null;
        setFocus(null);
        schedule();
    }

    /* A goes through X's own link, so X's router opens the post. */
    function activate() {
        if (!focusEl) return;
        var player = focusEl.querySelector('[data-testid="videoPlayer"]');
        if (player) {
            openViewer(player);
            return;
        }
        var id = idOf(focusEl);
        if (location.pathname.indexOf('/status/' + id) >= 0) return;
        var time = focusEl.querySelector('a[href*="/status/"] time');
        var link = time && time.closest('a');
        if (link) link.click();
    }

    function videoOf(element) {
        return element && element.querySelector('video');
    }

    /* The viewer's video, or else the focused post's. X can swap the <video> for a new one, so
       it's looked up each time. */
    function currentVideo() {
        return videoOf(viewer ? viewer.player : focusEl);
    }

    function togglePlay() {
        var video = currentVideo();
        if (!video) return;
        if (video.paused) play(video);
        else video.pause();
        showOsd();
    }

    function play(video) {
        var playing = video.play();
        if (playing && playing.catch) playing.catch(function (e) { log('play: ' + e.message); });
    }

    function seek(seconds) {
        var video = currentVideo();
        if (!video || !isFinite(video.duration)) return;
        video.currentTime = Math.max(0, Math.min(video.duration - 0.5, video.currentTime + seconds));
        showOsd();
    }

    /* -- the video viewer ------------------------------------------------------------------------- */

    /* The post's own player, full screen (the app's press counts as a user gesture, so the page
       may), with sound and our progress bar on top. */
    function openViewer(player, retried) {
        var video = videoOf(player);
        if (!video) {
            // X puts the video in once its play button is pressed.
            var button = player.querySelector('[role="button"], button');
            if (button) button.click();
            if (!retried) setTimeout(function () { if (player.isConnected) openViewer(player, true); }, 600);
            return;
        }
        var osd = document.createElement('div');
        osd.className = 'openbox-osd';
        osd.innerHTML = '<div class="state"></div><div class="track"><div class="played"></div></div><div class="time"></div>';
        player.appendChild(osd);
        viewer = { player: player, osd: osd, timer: 0, unmutes: 0 };
        player.requestFullscreen().catch(function (e) {
            log('full screen: ' + e.message);
            closeViewer();
        });
        video.muted = false;
        play(video);
        showOsd();
    }

    function closeViewer() {
        if (!viewer) return;
        var v = viewer;
        viewer = null;
        clearTimeout(v.timer);
        if (v.osd.parentNode) v.osd.parentNode.removeChild(v.osd);
        // Back in the timeline it plays the way X plays it there: muted.
        var video = videoOf(v.player);
        if (video) video.muted = true;
        if (document.fullscreenElement) document.exitFullscreen().catch(function () {});
    }

    function clock(seconds) {
        seconds = Math.max(0, Math.floor(seconds || 0));
        var s = seconds % 60;
        return Math.floor(seconds / 60) + ':' + (s < 10 ? '0' : '') + s;
    }

    function updateOsd() {
        var video = viewer && videoOf(viewer.player);
        if (!video) return;
        var osd = viewer.osd;
        osd.firstChild.className = 'state ' + (video.paused ? 'paused' : 'playing');
        osd.querySelector('.played').style.width = (video.duration ? video.currentTime / video.duration * 100 : 0) + '%';
        osd.lastChild.textContent = clock(video.currentTime) + ' / ' + clock(video.duration);
    }

    /* For a few seconds after each press, and for as long as it's paused. */
    function showOsd() {
        if (!viewer) return;
        updateOsd();
        viewer.osd.classList.add('on');
        clearTimeout(viewer.timer);
        viewer.timer = setTimeout(function () {
            var video = viewer && videoOf(viewer.player);
            if (video && !video.paused) viewer.osd.classList.remove('on');
        }, 3000);
    }

    /* Media events don't bubble, but they can be caught on the way down. */
    function onMedia(type, handler) {
        document.addEventListener(type, function (e) {
            if (viewer && e.target.tagName === 'VIDEO' && viewer.player.contains(e.target)) handler(e.target);
        }, true);
    }
    onMedia('timeupdate', updateOsd);
    onMedia('play', showOsd);
    onMedia('pause', showOsd);
    // X mutes its videos again as they start: in the viewer the sound stays on.
    onMedia('volumechange', function (video) {
        if (video.muted && viewer.unmutes++ < 20) video.muted = false;
    });

    document.addEventListener('fullscreenchange', function () {
        if (!document.fullscreenElement) closeViewer();
    });

    /* -- forms (sign-in) ---------------------------------------------------------------------------- */

    function isText(el) {
        return !!el && (el.tagName === 'TEXTAREA' || el.isContentEditable ||
            (el.tagName === 'INPUT' && TEXT_TYPES.test(el.type || 'text')));
    }

    function shown(el) {
        var rect = el.getBoundingClientRect();
        if (rect.width < 2 || rect.height < 2) return false;
        var style = getComputedStyle(el);
        return style.visibility !== 'hidden' && style.display !== 'none' && parseFloat(style.opacity) > 0.05;
    }

    /* What the D-pad can reach: the fields and buttons of X's dialog, or of the page if there's
       none. A button inside another (a link around a button, say) counts once, as the outer one. */
    function formDialog() {
        var dialogs = Array.prototype.filter.call(document.querySelectorAll('[role="dialog"], [aria-modal="true"]'), shown);
        return dialogs[dialogs.length - 1] || null;
    }

    function formFields() {
        var scope = formDialog() || document.body;
        if (!scope) return [];
        return Array.prototype.filter.call(scope.querySelectorAll(FIELDS), function (el) {
            if (el.querySelector('iframe') || (!isText(el) && POPUP_SIGN_IN.test(el.textContent))) return false;
            if (el.tagName === 'INPUT' && (el.type === 'checkbox' || el.type === 'radio') && !shown(el)) {
                // X draws its own box and hides the real one: the label stands in for it.
                return false;
            }
            if (!shown(el)) return false;
            var outer = el.parentElement && el.parentElement.closest(FIELDS);
            return !outer || isText(el) || !scope.contains(outer);
        });
    }

    /* The ring goes round a text box's whole frame: X draws it on the label around the input. */
    function ringOf(el) {
        return (isText(el) && el.closest('label')) || el;
    }

    function setFormFocus(el) {
        var old = document.querySelectorAll('[data-openbox-ring]');
        for (var i = 0; i < old.length; i++) old[i].removeAttribute('data-openbox-ring');
        formEl = el;
        if (!el) return;
        ringOf(el).setAttribute('data-openbox-ring', '');
        // Moving off a text box closes the keyboard's caret there; the box itself only takes the
        // focus (and the keyboard comes up) on A.
        var active = document.activeElement;
        if (active && active !== el && active !== document.body && active.blur) active.blur();
        if (!isText(el) && el.focus) el.focus({ preventScroll: true });
        ringOf(el).scrollIntoView({ block: 'nearest' });
    }

    /* The first empty text box, as X's own autofocus would pick, or else the first button. */
    function defaultField(fields) {
        for (var i = 0; i < fields.length; i++) if (isText(fields[i]) && !fields[i].value) return fields[i];
        for (var j = 0; j < fields.length; j++) if (isText(fields[j])) return fields[j];
        return fields[0] || null;
    }

    function refocusForm() {
        var fields = formFields();
        if (formEl && fields.indexOf(formEl) >= 0) {
            var ring = ringOf(formEl);
            if (!ring.hasAttribute('data-openbox-ring')) ring.setAttribute('data-openbox-ring', '');
            return;
        }
        // A new step of the flow (the password after the username, say): its first field.
        setFormFocus(defaultField(fields));
    }

    /* The nearest field that way, going by the middle of each: straight lines first. */
    function moveForm(action) {
        var fields = formFields();
        if (!formEl || fields.indexOf(formEl) < 0) {
            setFormFocus(defaultField(fields));
            return;
        }
        var from = formEl.getBoundingClientRect();
        var fx = from.left + from.width / 2, fy = from.top + from.height / 2;
        var vertical = action === 'up' || action === 'down';
        var sign = action === 'down' || action === 'right' ? 1 : -1;
        var best = null, bestScore = Infinity;
        fields.forEach(function (el) {
            if (el === formEl || ringOf(el) === ringOf(formEl)) return;
            var rect = el.getBoundingClientRect();
            var dx = rect.left + rect.width / 2 - fx, dy = rect.top + rect.height / 2 - fy;
            var along = (vertical ? dy : dx) * sign, across = Math.abs(vertical ? dx : dy);
            if (along < 4) return;
            var score = along + across * (vertical ? 0.5 : 3);
            if (score < bestScore) {
                bestScore = score;
                best = el;
            }
        });
        if (best) setFormFocus(best);
    }

    /* A press of a button. X's sign-in pages answer pointer events rather than clicks on their
       own div buttons (the arrow in the corner), and X's main app answers clicks: both get sent.
       Native controls only need the click. */
    function tap(el) {
        if (!/^(?:BUTTON|A|INPUT|SELECT|TEXTAREA|LABEL)$/.test(el.tagName)) {
            var rect = el.getBoundingClientRect();
            var init = {
                bubbles: true, cancelable: true, composed: true, view: window, button: 0, buttons: 1,
                clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2,
                pointerId: 1, pointerType: 'mouse', isPrimary: true
            };
            el.dispatchEvent(new PointerEvent('pointerdown', init));
            el.dispatchEvent(new MouseEvent('mousedown', init));
            init.buttons = 0;
            el.dispatchEvent(new PointerEvent('pointerup', init));
            el.dispatchEvent(new MouseEvent('mouseup', init));
        }
        el.click();
    }

    function pressField() {
        var el = formEl;
        if (!el || !el.isConnected) return;
        if (isText(el)) {
            // Focus again even if it already has it: that's what brings the console's keyboard up.
            if (document.activeElement === el) el.blur();
            el.focus();
            el.click();
            if (navigator.virtualKeyboard && navigator.virtualKeyboard.show) {
                try { navigator.virtualKeyboard.show(); } catch (e) {}
            }
            log('Typing in ' + (el.name || el.type || el.tagName));
        } else if (el.tagName === 'SELECT' && el.showPicker) {
            try { el.showPicker(); } catch (e) { el.focus(); }
        } else {
            tap(el);
        }
    }

    /* B: off the text box being typed in, then X's own arrow in the corner of its dialog (back a
       step of the flow, or out of it), then out of X from the page behind. */
    function formBack() {
        if (isTyping()) {
            document.activeElement.blur();
            return;
        }
        var dialog = formDialog();
        if (!dialog) {
            post('app.exit', {});
            return;
        }
        var corner = dialog.getBoundingClientRect();
        var arrow = formFields().filter(function (el) {
            var rect = el.getBoundingClientRect();
            return !el.textContent.trim() && !isText(el) &&
                rect.left - corner.left < 120 && rect.top - corner.top < 120;
        })[0];
        if (!arrow) {
            post('app.exit', {});
            return;
        }
        // On the flow's first step the arrow may do nothing: then B leaves X, as it would anywhere.
        var before = location.href + formFields().length;
        var step = formFields()[0];
        tap(arrow);
        setTimeout(function () {
            var fields = formFields();
            if (location.href + fields.length === before && fields[0] === step) post('app.exit', {});
        }, 500);
    }

    function handleForm(action) {
        switch (action) {
            case 'up': case 'down': case 'left': case 'right': moveForm(action); break;
            case 'select': pressField(); break;
            case 'back': formBack(); break;
        }
        schedule();
    }

    /* -- feeds ------------------------------------------------------------------------------------- */

    function homeTabs() {
        return Array.prototype.slice.call(document.querySelectorAll('[data-testid="primaryColumn"] [role="tablist"] [role="tab"]'));
    }

    function currentFeed() {
        if (isVideos()) return feedNames.length;
        if (!isHome()) return -1;
        var tabs = homeTabs();
        for (var i = 0; i < tabs.length; i++) {
            if (tabs[i].getAttribute('aria-selected') === 'true') return i;
        }
        return 0;
    }

    function switchFeed(step) {
        var current = currentFeed();
        var target = current < 0 ? (step > 0 ? 0 : feedNames.length) : current + step;
        if (target < 0 || target > feedNames.length || target === current) return;
        if (target === feedNames.length) {
            go(VIDEOS_URL);
        } else if (isHome()) {
            selectTab(target);
        } else {
            pendingTab = target;
            go('/home');
        }
    }

    function selectTab(index) {
        var tab = homeTabs()[index];
        if (!tab) return false;
        if (tab.getAttribute('aria-selected') !== 'true') {
            tab.click();
            focusId = null;
            setFocus(null);
            window.scrollTo(0, 0);
        }
        return true;
    }

    /* Through X's router, the way its own links go, so the page doesn't load again. */
    function go(path) {
        history.pushState({}, '', path);
        window.dispatchEvent(new PopStateEvent('popstate', { state: {} }));
    }

    function back() {
        if (viewer) {
            closeViewer();
        } else if (isHome() && on) {
            post('app.exit', {});
        } else if (window.navigation && window.navigation.canGoBack) {
            history.back();
        } else if (on) {
            go('/home');
        } else {
            post('app.exit', {});
        }
    }

    function renderBar() {
        if (!document.body) return;
        if (!bar || !bar.isConnected) {
            bar = document.createElement('div');
            bar.id = 'openbox-x-bar';
            document.body.appendChild(bar);
            barHtml = '';
        }
        var tabs = homeTabs();
        if (isHome() && tabs.length) {
            feedNames = tabs.map(function (tab) { return tab.textContent.trim(); });
        }
        var current = currentFeed();
        var names = feedNames.concat(['Videos']);
        var html = '<span class="button">LB</span>';
        for (var i = 0; i < names.length; i++) {
            html += '<span class="feed' + (i === current ? ' on' : '') + '">' + escapeHtml(names[i]) + '</span>';
        }
        html += '<span class="button">RB</span><span class="hints">' +
            '<span class="hint"><span class="button round a">A</span>' + (focusEl && focusEl.querySelector('[data-testid="videoPlayer"]') ? 'Watch' : 'Open') + '</span>' +
            '<span class="hint"><span class="button round b">B</span>' + (isHome() ? 'Exit' : 'Back') + '</span></span>';
        if (html !== barHtml) bar.innerHTML = barHtml = html;
        var background = getComputedStyle(document.body).backgroundColor;
        if (background && background !== 'rgba(0, 0, 0, 0)') {
            bar.style.setProperty('--openbox-bar-bg', background.replace('rgb(', 'rgba(').replace(')', ', 0.92)'));
        }
    }

    function escapeHtml(text) {
        return text.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
    }

    /* -- keeping up with X ------------------------------------------------------------------------- */

    /* The reply box under a post shares the post's timeline entry: what's hidden is the box's
       own part of it. */
    function hideComposers() {
        var boxes = document.querySelectorAll('[data-testid="primaryColumn"] section [data-testid="tweetTextarea_0"]');
        for (var i = 0; i < boxes.length; i++) {
            var part = boxes[i];
            while (part.parentElement && !part.parentElement.querySelector('article') &&
                   part.parentElement.getAttribute('data-testid') !== 'cellInnerDiv') {
                part = part.parentElement;
            }
            if (!part.hasAttribute('data-openbox-hide')) part.setAttribute('data-openbox-hide', '');
        }
    }

    function update() {
        scheduled = false;
        if (!document.documentElement) return;
        if (location.href !== url) {
            // A new page: the post that had the focus when we were last on it, if any.
            url = location.href;
            focusEl && focusEl.removeAttribute('data-openbox-focus');
            focusEl = null;
            focusId = focusByUrl[url] || null;
        }
        updateMode();
        dressPage();
        if (!on) {
            if (document.body) refocusForm();
            return;
        }
        if (formEl) setFormFocus(null);
        if (pendingTab >= 0 && isHome() && selectTab(pendingTab)) pendingTab = -1;
        hideComposers();
        refocus();
        renderBar();
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(update);
    }

    function isTyping() {
        var el = document.activeElement;
        return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
    }

    function handle(action, repeat) {
        if (!on) {
            handleForm(action);
            return;
        }
        if (viewer) {
            if (action === 'back') closeViewer();
            else if (action === 'select' || action === 'x') togglePlay();
            else if (action === 'lt' || action === 'left') seek(-SEEK_SECONDS);
            else if (action === 'rt' || action === 'right') seek(SEEK_SECONDS);
            return;
        }
        switch (action) {
            case 'up': move(-1, repeat); break;
            case 'down': move(1, repeat); break;
            case 'select': activate(); break;
            case 'x': togglePlay(); break;
            case 'lt': seek(-SEEK_SECONDS); break;
            case 'rt': seek(SEEK_SECONDS); break;
            case 'lb': switchFeed(-1); break;
            case 'rb': switchFeed(1); break;
            case 'y': toTop(); break;
            case 'back': back(); break;
        }
        schedule();
    }

    webview.addEventListener('message', function (event) {
        var msg = event.data;
        if (msg && msg.type === 'input') handle(msg.action, msg.repeat);
    });

    /* The controller's messages are the input. On the console the browser also makes key events
       and a mouse pointer of the controller, which would move the page a second time (see
       remote.js), so every key is dropped, except in a text box being typed in: there only the
       controller's own (gamepad key codes 195-218) are, and what's typed on the console's keyboard
       gets through. X's own shortcuts (L likes a post, U mutes) never see a key. Off the console
       the keyboard stands in for the controller. */
    ['keydown', 'keyup', 'keypress'].forEach(function (type) {
        window.addEventListener(type, function (e) {
            if (!e.isTrusted) return;
            var action = KEY_ACTION[e.key];
            if (!on && isTyping() && !(e.keyCode >= 195 && e.keyCode <= 218) && !(action === 'back' && !config.xbox)) {
                // Typing in a text box: the keys are the box's.
                return;
            }
            e.preventDefault();
            e.stopImmediatePropagation();
            if (type !== 'keydown' || config.xbox || !action) return;
            if (on) handle(action, e.repeat);
            else handleForm(action);
        }, true);
    });

    if (config.xbox) {
        ['pointerdown', 'pointerup', 'pointermove', 'pointerover', 'mousedown', 'mouseup', 'mousemove',
         'mouseover', 'click', 'dblclick', 'auxclick', 'contextmenu', 'wheel', 'mousewheel'].forEach(function (type) {
            window.addEventListener(type, function (e) {
                if (!e.isTrusted) return;
                e.preventDefault();
                e.stopImmediatePropagation();
            }, { capture: true, passive: false });
        });
    }

    new MutationObserver(schedule).observe(document, { childList: true, subtree: true });
    window.addEventListener('popstate', schedule);
    window.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule);
    updateMode();
    schedule();
    log('X: ' + window.innerWidth + 'x' + window.innerHeight + ' at ' + window.devicePixelRatio + 'x, ' + navigator.userAgent);
})();
