(function () {
    'use strict';

    if (window.JellyTrendsInit) {
        return;
    }
    window.JellyTrendsInit = true;

    var ROOT_ID = 'jellytrends-root';
    var FRESH_MS = 5 * 60 * 1000;
    var RETRY_AFTER_FAILURE_MS = 30 * 1000;
    var PLAIN_KEY = 'jellytrends.plainScroller';

    var state = {
        rows: null,
        rowsAt: 0,
        userId: null,
        inflight: null,
        failedAt: 0,
        mounting: false,
        timer: null,
        observer: null,
        observedNode: null,
        waitTries: 0
    };

    function apiClient() {
        return window.ApiClient || null;
    }

    function loggedIn() {
        var client = apiClient();
        return !!(client && typeof client.accessToken === 'function' && client.accessToken());
    }

    function currentUserId() {
        var client = apiClient();
        return client && typeof client.getCurrentUserId === 'function' ? client.getCurrentUserId() : null;
    }

    /**
     * The home route is '#/home' on Jellyfin 10.10+ (optionally with '?tab=0'). Older builds and
     * some app wrappers use the '#!/' prefix, so it is normalised before comparing.
     */
    function onHome() {
        var hash = (location.hash || '').replace('#!', '#');
        return hash.indexOf('#/home') === 0 || hash === '#/' || hash === '#' || hash === '';
    }

    /**
     * The rows mount inside the home sections container, as its first child.
     *
     * They must live inside it rather than beside it: hero plugins such as Media Bar offset
     * '.homeSectionsContainer' (top: 65vh) to clear a full-bleed slideshow, and anything
     * mounted outside that container misses the offset and ends up hidden underneath it.
     */
    function getMountTarget() {
        return document.querySelector('#homeTab .homeSectionsContainer') ||
            document.querySelector('#homeTab .sections') ||
            document.querySelector('.homeSectionsContainer');
    }

    function getRoot() {
        return document.getElementById(ROOT_ID);
    }

    function removeRoot() {
        var existing = getRoot();
        if (existing) {
            existing.remove();
        }
    }

    function clamp(value, min, max) {
        var parsed = parseInt(value, 10);
        if (isNaN(parsed)) {
            parsed = 100;
        }
        return Math.max(min, Math.min(max, parsed));
    }

    function usePlainScroller() {
        try {
            if (window.localStorage && localStorage.getItem(PLAIN_KEY) === '1') {
                return true;
            }
        } catch (e) { /* storage may be blocked */ }
        // Jellyfin registers its scroller as a customized built-in. When it is missing (older
        // builds, trimmed clients) the plain CSS scroller below is used instead.
        return !(window.customElements && customElements.get('emby-scroller'));
    }

    function markPlainScroller() {
        try {
            if (window.localStorage) {
                localStorage.setItem(PLAIN_KEY, '1');
            }
        } catch (e) { /* storage may be blocked */ }
    }

    function detailsHref(itemId) {
        var client = apiClient();
        // Jellyfin's own cards always carry serverId. Without it the details route cannot
        // resolve which server the item belongs to and navigation lands nowhere.
        var serverId = client && typeof client.serverId === 'function' ? client.serverId() : null;
        var href = '#/details?id=' + encodeURIComponent(itemId);
        return serverId ? href + '&serverId=' + encodeURIComponent(serverId) : href;
    }

    function posterUrl(item) {
        var client = apiClient();
        if (!item.ImageTag || !client || typeof client.getImageUrl !== 'function') {
            return null;
        }
        // The tag makes the URL cacheable, exactly like Jellyfin's own cards.
        return client.getImageUrl(item.Id, { type: 'Primary', maxHeight: 450, quality: 90, tag: item.ImageTag });
    }

    function el(tag, className) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        return node;
    }

    function createCard(item, showRank) {
        // Mirrors the markup Jellyfin's own cardBuilder emits for an overflow portrait card,
        // so the rows inherit native sizing, hover, spacing and typography.
        var href = detailsHref(item.Id);
        var name = item.Name || '';

        var card = el('div', 'card overflowPortraitCard card-hoverable jellytrends-card');
        card.setAttribute('data-id', item.Id);

        var cardBox = el('div', 'cardBox cardBox-bottompadded');
        var scalable = el('div', 'cardScalable');
        scalable.appendChild(el('div', 'cardPadder cardPadder-overflowPortrait'));

        var link = el('a', 'cardImageContainer coveredImage cardContent itemAction jellytrends-link');
        link.setAttribute('data-action', 'link');
        link.href = href;
        link.title = name;
        link.setAttribute('aria-label', name);

        var image = posterUrl(item);
        if (image) {
            var img = el('img', 'jellytrends-poster');
            img.alt = '';
            img.decoding = 'async';
            img.loading = 'lazy';
            img.draggable = false;
            img.src = image;
            link.appendChild(img);
        } else {
            link.classList.add('jellytrends-noimage');
            link.appendChild(document.createTextNode(name));
        }

        if (showRank) {
            var badge = el('span', 'jellytrends-rank');
            badge.textContent = '#' + item.Rank;
            link.appendChild(badge);
        }

        scalable.appendChild(link);
        cardBox.appendChild(scalable);

        var first = el('div', 'cardText cardText-first cardTextCentered jellytrends-text');
        var bdi = document.createElement('bdi');
        var textLink = el('a', 'itemAction textActionButton jellytrends-textlink');
        textLink.setAttribute('data-action', 'link');
        textLink.href = href;
        textLink.title = name;
        textLink.textContent = name;
        bdi.appendChild(textLink);
        first.appendChild(bdi);
        cardBox.appendChild(first);

        if (item.ProductionYear) {
            var second = el('div', 'cardText cardText-secondary cardTextCentered jellytrends-text');
            var yearBdi = document.createElement('bdi');
            yearBdi.textContent = String(item.ProductionYear);
            second.appendChild(yearBdi);
            cardBox.appendChild(second);
        }

        card.appendChild(cardBox);
        return card;
    }

    function createSection(title, items, showRank, plain) {
        var section = el('div', 'verticalSection jellytrends-section');

        var titleContainer = el('div', 'sectionTitleContainer sectionTitleContainer-cards padded-left');
        var heading = el('h2', 'sectionTitle sectionTitle-cards');
        heading.textContent = title;
        titleContainer.appendChild(heading);
        section.appendChild(titleContainer);

        // Cards are assembled off-document so the browser lays out once, not once per card.
        var fragment = document.createDocumentFragment();
        for (var i = 0; i < items.length; i++) {
            fragment.appendChild(createCard(items[i], showRank));
        }

        if (plain) {
            var row = el('div', 'itemsContainer padded-left padded-right jellytrends-row');
            row.appendChild(fragment);
            section.appendChild(row);
            return section;
        }

        // Jellyfin's own scroller: arrow buttons, wheel/touch handling and TV focus. The
        // children are in place before it enters the document so it measures them on connect.
        var scroller = document.createElement('div', { is: 'emby-scroller' });
        scroller.setAttribute('is', 'emby-scroller');
        scroller.className = 'padded-top-focusscale padded-bottom-focusscale jellytrends-scroller';
        scroller.setAttribute('data-horizontal', 'true');
        scroller.setAttribute('data-mousewheel', 'false');
        scroller.setAttribute('data-centerfocus', 'true');

        var slider = el('div', 'itemsContainer scrollSlider focuscontainer-x jellytrends-slider');
        slider.appendChild(fragment);
        scroller.appendChild(slider);
        section.appendChild(scroller);
        return section;
    }

    function signature(rows) {
        try {
            return JSON.stringify(rows);
        } catch (e) {
            return String(Date.now());
        }
    }

    function mount(rows, sig) {
        var target = getMountTarget();
        if (!target) {
            return false;
        }

        var movies = rows.Movies || [];
        var shows = rows.Shows || [];

        state.mounting = true;
        try {
            removeRoot();
            if (!movies.length && !shows.length) {
                return true;
            }

            var plain = usePlainScroller();
            var root = el('div', 'jellytrends-root');
            root.id = ROOT_ID;
            root.setAttribute('data-sig', sig);
            root.setAttribute('data-plain', plain ? '1' : '0');
            root.style.setProperty('--jt-card-scale', String(clamp(rows.CardScalePercent, 60, 180) / 100));
            root.style.setProperty('--jt-text-scale', String(clamp(rows.TextScalePercent, 70, 180) / 100));

            var count = clamp(rows.MaxDisplayItems, 1, 50);
            var showRank = rows.ShowOnlineRank !== false;

            if (movies.length) {
                root.appendChild(createSection(rows.MoviesTitle || 'Top ' + count + ' Movies In Your Library', movies, showRank, plain));
            }
            if (shows.length) {
                root.appendChild(createSection(rows.ShowsTitle || 'Top ' + count + ' Shows In Your Library', shows, showRank, plain));
            }

            target.insertBefore(root, target.firstChild);

            if (!plain) {
                verifyNativeScroller(rows, sig);
            }
            return true;
        } finally {
            // MutationObserver callbacks run as microtasks, so this flag is still set while
            // the observer reports our own insertion.
            setTimeout(function () {
                state.mounting = false;
            }, 0);
        }
    }

    /**
     * Guards against the native scroller initialising badly on a client this was not tested
     * on: if the first card never gets a size, fall back to the plain scroller for good.
     */
    function verifyNativeScroller(rows, sig) {
        setTimeout(function () {
            var root = getRoot();
            if (!root || !root.isConnected || !onHome() || root.getAttribute('data-sig') !== sig) {
                return;
            }
            var card = root.querySelector('.jellytrends-card');
            // A hidden tab (e.g. Favorites) also reports zero, so only judge a visible root.
            if (card && root.offsetParent !== null && card.offsetWidth === 0) {
                markPlainScroller();
                mount(rows, sig);
            }
        }, 700);
    }

    function syncDom(rows) {
        if (!rows || !rows.Enabled) {
            removeRoot();
            return;
        }

        var target = getMountTarget();
        var root = getRoot();
        var sig = signature(rows);

        // Already showing exactly this: leave the DOM alone. Replacing nodes under the cursor
        // is what makes a click land on nothing.
        if (root && root.isConnected && target && root.parentNode === target && root.getAttribute('data-sig') === sig) {
            return;
        }

        mount(rows, sig);
    }

    /**
     * One request returns the matched rows and the display settings together. Matching runs
     * on the server, so the client never downloads the library to work out what it owns.
     */
    function fetchRows() {
        if (state.inflight) {
            return state.inflight;
        }

        var client = apiClient();
        var userId = currentUserId();

        state.inflight = client.getJSON(client.getUrl('JellyTrends/rows')).then(function (rows) {
            if (userId !== currentUserId()) {
                return null; // the account changed while this was in flight
            }
            state.rows = rows || {};
            state.rowsAt = Date.now();
            state.userId = userId;
            state.failedAt = 0;
            return state.rows;
        }).catch(function (error) {
            state.failedAt = Date.now();
            if (window.console && console.warn) {
                console.warn('JellyTrends could not load rows', error);
            }
            return null;
        }).then(function (rows) {
            state.inflight = null;
            return rows;
        });

        return state.inflight;
    }

    function ensure() {
        if (!onHome()) {
            return;
        }

        if (!apiClient() || !loggedIn()) {
            // jellyfin-web creates ApiClient and signs in asynchronously on a cold start.
            if (state.waitTries++ < 120) {
                scheduleEnsure(500);
            }
            return;
        }
        state.waitTries = 0;

        var userId = currentUserId();
        if (state.userId !== null && state.userId !== userId) {
            // Never show one account's titles to another after a sign-out/sign-in.
            state.rows = null;
            state.rowsAt = 0;
            removeRoot();
        }

        observeHome();

        // Cached rows draw immediately; the refresh below only repaints if something changed.
        if (state.rows) {
            syncDom(state.rows);
        }

        var stale = !state.rows || (Date.now() - state.rowsAt) > FRESH_MS;
        var coolingDown = state.failedAt && (Date.now() - state.failedAt) < RETRY_AFTER_FAILURE_MS;
        if (stale && !coolingDown) {
            fetchRows().then(function (rows) {
                if (rows && onHome()) {
                    syncDom(rows);
                }
            });
        }
    }

    function scheduleEnsure(delay) {
        // Throttle rather than debounce: a page that mutates continuously must still get its
        // rows back instead of resetting the timer forever.
        if (state.timer) {
            return;
        }
        state.timer = setTimeout(function () {
            state.timer = null;
            ensure();
        }, delay || 100);
    }

    /**
     * Watches only the home tab, and only reacts when the rows have actually gone missing
     * (Jellyfin rewrites the sections container whenever home sections reload).
     *
     * Observing document.body would fire on every unrelated mutation on the page. That is
     * especially costly alongside Media Bar, whose slideshow mutates continuously.
     */
    function observeHome() {
        if (typeof MutationObserver !== 'function') {
            return;
        }

        var homeTab = document.querySelector('#homeTab');
        if (!homeTab || homeTab === state.observedNode) {
            return;
        }

        if (state.observer) {
            state.observer.disconnect();
        }

        state.observedNode = homeTab;
        state.observer = new MutationObserver(function () {
            if (state.mounting) {
                return;
            }
            var root = getRoot();
            if (!root || !root.isConnected) {
                scheduleEnsure(60);
            }
        });
        state.observer.observe(homeTab, { childList: true, subtree: true });
    }

    function handleNavigation() {
        if (onHome()) {
            scheduleEnsure(60);
        }
    }

    /**
     * Opens the item directly instead of leaving it to other click handlers on the page, so a
     * card always navigates. Modified clicks keep their normal "open in new tab" behaviour.
     */
    function onDocumentClick(event) {
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }

        var root = getRoot();
        if (!root || !root.contains(event.target)) {
            return;
        }

        var node = event.target;
        while (node && node !== root && !(node.tagName === 'A' && node.getAttribute('href'))) {
            node = node.parentNode;
        }

        var href = node && node !== root ? node.getAttribute('href') : null;
        if (!href || href.charAt(0) !== '#') {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        location.hash = href;
    }

    function init() {
        document.addEventListener('click', onDocumentClick, true);

        window.addEventListener('hashchange', handleNavigation, true);
        window.addEventListener('popstate', handleNavigation, true);
        // jellyfin-web fires this each time a page becomes visible, including when it returns
        // to a cached home page without any hash change.
        document.addEventListener('viewshow', handleNavigation, true);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                handleNavigation();
            }
        });

        // The home tab does not exist yet on a cold load, so watch for it once, cheaply, and
        // hand off to the narrow observer as soon as it appears.
        if (typeof MutationObserver === 'function') {
            var bootstrap = new MutationObserver(function () {
                if (document.querySelector('#homeTab .homeSectionsContainer')) {
                    bootstrap.disconnect();
                    scheduleEnsure(0);
                }
            });
            bootstrap.observe(document.documentElement, { childList: true, subtree: true });
            setTimeout(function () { bootstrap.disconnect(); }, 30000);
        }

        scheduleEnsure(0);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
