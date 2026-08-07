(function () {
    if (window.JellyTrendsInit) {
        return;
    }
    window.JellyTrendsInit = true;

    var ROOT_ID = 'jellytrends-root';
    var ROWS_CACHE_MS = 5 * 60 * 1000;

    var state = {
        busy: false,
        mounting: false,
        runId: 0,
        ensureTimer: null,
        observer: null,
        observedNode: null,
        rows: null,
        rowsAt: 0
    };

    function apiClient() {
        return window.ApiClient || null;
    }

    /**
     * The home route is '#/home' on Jellyfin 10.11. Older builds and some app wrappers still
     * use the '#!/' prefix, so it is normalised before comparing.
     */
    function onHome() {
        var hash = (location.hash || '').replace('#!', '#');
        return hash.indexOf('#/home') === 0 || hash === '#/' || hash === '#';
    }

    function isPlaybackActive() {
        return !!(
            document.querySelector('.videoPlayerContainer:not(.hide)') ||
            document.querySelector('.htmlVideoPlayerContainer:not(.hide)') ||
            document.querySelector('.videoOsd:not(.hide)')
        );
    }

    /**
     * The rows mount inside the home sections container, as its first child.
     *
     * They must live inside it rather than beside it: hero plugins such as Media Bar offset
     * '.homeSectionsContainer' (top: 65vh) to clear a full-bleed slideshow, and anything
     * mounted outside that container misses the offset and ends up hidden underneath the
     * slideshow. Jellyfin rewrites this container's innerHTML whenever home sections reload,
     * which drops the rows; the observer re-attaches them from cache.
     */
    function getMountTarget() {
        return document.querySelector('#homeTab .homeSectionsContainer') ||
            document.querySelector('#homeTab .sections') ||
            document.querySelector('.homeSectionsContainer') ||
            document.querySelector('#homeTab');
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
        if (!item.HasPrimaryImage || !client || typeof client.getImageUrl !== 'function') {
            return null;
        }
        return client.getImageUrl(item.Id, { type: 'Primary', fillHeight: 330, quality: 90 });
    }

    function createCard(item, rank, showRank) {
        // Mirrors the markup Jellyfin's own cardBuilder emits for an overflow portrait card,
        // so the rows inherit native sizing, hover, spacing and typography.
        var card = document.createElement('div');
        card.className = 'card overflowPortraitCard card-hoverable jellytrends-card';

        var cardBox = document.createElement('div');
        cardBox.className = 'cardBox cardBox-bottompadded';

        var scalable = document.createElement('div');
        scalable.className = 'cardScalable';

        var padder = document.createElement('div');
        padder.className = 'cardPadder cardPadder-overflowPortrait';

        var link = document.createElement('a');
        link.className = 'cardImageContainer coveredImage cardContent itemAction jellytrends-link';
        link.setAttribute('data-action', 'link');
        link.href = detailsHref(item.Id);
        link.title = item.Name || '';

        var image = posterUrl(item);
        if (image) {
            link.style.backgroundImage = 'url(\'' + image.replace(/'/g, "\\'") + '\')';
        } else {
            link.classList.add('jellytrends-noimage');
            link.textContent = item.Name || '';
        }

        if (showRank) {
            var badge = document.createElement('span');
            badge.className = 'jellytrends-rank';
            badge.textContent = '#' + rank;
            link.appendChild(badge);
        }

        scalable.appendChild(padder);
        scalable.appendChild(link);

        var text = document.createElement('div');
        text.className = 'cardText cardTextCentered jellytrends-text';
        var textLink = document.createElement('a');
        textLink.className = 'itemAction jellytrends-textlink';
        textLink.setAttribute('data-action', 'link');
        textLink.href = link.href;
        textLink.textContent = item.Name || '';
        text.appendChild(textLink);

        cardBox.appendChild(scalable);
        cardBox.appendChild(text);
        card.appendChild(cardBox);
        return card;
    }

    function createSection(title, items, showRank) {
        var section = document.createElement('div');
        section.className = 'verticalSection jellytrends-section';

        var heading = document.createElement('h2');
        heading.className = 'sectionTitle sectionTitle-cards padded-left';
        heading.textContent = title;
        section.appendChild(heading);

        var row = document.createElement('div');
        row.className = 'itemsContainer padded-left padded-right jellytrends-row';

        // Cards are assembled off-document so the browser lays out once, not once per card.
        var fragment = document.createDocumentFragment();
        for (var i = 0; i < items.length; i++) {
            fragment.appendChild(createCard(items[i], items[i].Rank, showRank));
        }
        row.appendChild(fragment);

        section.appendChild(row);
        return section;
    }

    function mount(rows) {
        var target = getMountTarget();
        if (!target) {
            return false;
        }

        state.mounting = true;
        try {
            removeRoot();

            var movies = rows.Movies || [];
            var shows = rows.Shows || [];
            if (!movies.length && !shows.length) {
                return true;
            }

            var root = document.createElement('div');
            root.id = ROOT_ID;
            root.className = 'jellytrends-root';
            root.style.setProperty('--jt-card-scale', String(clamp(rows.CardScalePercent, 60, 180) / 100));
            root.style.setProperty('--jt-text-scale', String(clamp(rows.TextScalePercent, 70, 180) / 100));

            var count = clamp(rows.MaxDisplayItems, 1, 50);
            var showRank = rows.ShowOnlineRank !== false;

            if (movies.length) {
                root.appendChild(createSection('Top ' + count + ' Movies In Your Library', movies, showRank));
            }
            if (shows.length) {
                root.appendChild(createSection('Top ' + count + ' Shows In Your Library', shows, showRank));
            }

            target.insertBefore(root, target.firstChild);
            return true;
        } finally {
            // Let the observer settle before it starts reacting to DOM changes again.
            setTimeout(function () {
                state.mounting = false;
            }, 0);
        }
    }

    function clamp(value, min, max) {
        var parsed = parseInt(value, 10);
        if (isNaN(parsed)) {
            parsed = 100;
        }
        return Math.max(min, Math.min(max, parsed));
    }

    /**
     * One request returns the matched rows and the display settings together. Matching runs
     * on the server, so the client never downloads the library to work out what it owns.
     */
    function loadRows() {
        var now = Date.now();
        if (state.rows && (now - state.rowsAt) < ROWS_CACHE_MS) {
            return Promise.resolve(state.rows);
        }

        var client = apiClient();
        return client.getJSON(client.getUrl('JellyTrends/rows')).then(function (rows) {
            state.rows = rows || {};
            state.rowsAt = Date.now();
            return state.rows;
        });
    }

    function run() {
        if (state.busy || !apiClient() || !onHome() || isPlaybackActive()) {
            return;
        }

        var runId = ++state.runId;
        state.busy = true;

        loadRows().then(function (rows) {
            if (runId !== state.runId || !onHome() || isPlaybackActive()) {
                return;
            }
            if (!rows || !rows.Enabled) {
                // Keep the cached answer so a disabled plugin does not re-request on every
                // DOM mutation; only the mount is skipped.
                removeRoot();
                return;
            }
            mount(rows);
        }).catch(function (error) {
            if (window.console && console.warn) {
                console.warn('JellyTrends failed to render', error);
            }
        }).finally(function () {
            state.busy = false;
        });
    }

    /**
     * Re-attaches the rows when Jellyfin rebuilds the home sections, and tears them down as
     * soon as the user leaves home or starts playback.
     */
    function ensure() {
        if (!onHome() || isPlaybackActive()) {
            removeRoot();
            return;
        }

        observeHome();

        if (getRoot()) {
            return;
        }

        if (state.rows && state.rows.Enabled && mount(state.rows)) {
            return;
        }

        run();
    }

    function scheduleEnsure(delay) {
        // Throttle rather than debounce: a page that mutates continuously must still get its
        // rows back instead of resetting the timer forever.
        if (state.ensureTimer) {
            return;
        }
        state.ensureTimer = setTimeout(function () {
            state.ensureTimer = null;
            ensure();
        }, delay || 250);
    }

    /**
     * Watches only the home tab.
     *
     * Observing document.body would fire on every unrelated mutation on the page. That is
     * especially costly alongside Media Bar, whose slideshow lives directly on document.body
     * and mutates continuously as slides transition, progress bars advance and artwork loads.
     */
    function observeHome() {
        if (typeof MutationObserver !== 'function') {
            return;
        }

        var homeTab = document.querySelector('#homeTab') || document.querySelector('#indexPage');
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
            scheduleEnsure(400);
        });
        state.observer.observe(homeTab, { childList: true, subtree: true });
    }

    function handleNavigation() {
        state.runId++;
        if (!onHome() || isPlaybackActive()) {
            removeRoot();
            return;
        }
        scheduleEnsure(300);
    }

    function init() {
        window.addEventListener('hashchange', handleNavigation, true);
        window.addEventListener('popstate', handleNavigation, true);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                scheduleEnsure(300);
            }
        });

        // The home tab does not exist yet on a cold load, so watch for it once, cheaply, and
        // hand off to the narrow observer as soon as it appears.
        if (typeof MutationObserver === 'function') {
            var bootstrap = new MutationObserver(function () {
                if (document.querySelector('#homeTab')) {
                    bootstrap.disconnect();
                    scheduleEnsure(150);
                }
            });
            bootstrap.observe(document.documentElement, { childList: true, subtree: true });
        }

        scheduleEnsure(800);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
