(function () {
    if (window.JellyTrendsInit) {
        return;
    }
    window.JellyTrendsInit = true;

    var ROOT_ID = 'jellytrends-root';
    var TRENDING_CACHE_MS = 5 * 60 * 1000;
    var LIBRARY_CACHE_MS = 15 * 60 * 1000;

    var state = {
        busy: false,
        mounting: false,
        runId: 0,
        ensureTimer: null,
        config: null,
        rendered: null,
        trendingCache: null,
        trendingCacheAt: 0,
        libraryCache: {},
        libraryCacheAt: {}
    };

    function normalizeTitle(name) {
        return (name || '')
            .toLowerCase()
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/&/g, ' and ')
            .replace(/[^a-z0-9 ]/g, ' ')
            .replace(/\b(the|a|an)\b/g, ' ')
            .replace(/\s+/g, ' ')
            .trim();
    }

    function apiClient() {
        return window.ApiClient || null;
    }

    function getCurrentUserId() {
        var client = apiClient();
        if (!client) {
            return null;
        }
        if (typeof client.getCurrentUserId === 'function') {
            return client.getCurrentUserId();
        }
        if (client._serverInfo && client._serverInfo.UserId) {
            return client._serverInfo.UserId;
        }
        return null;
    }

    /**
     * The home route is '#/home' on Jellyfin 10.11 but older builds and some wrappers
     * still use the '#!/' prefix or the legacy '.html' suffix.
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
     * Jellyfin rewrites the innerHTML of '.sections' every time home sections reload, so
     * the rows are mounted as a sibling above that container instead of inside it.
     */
    function getMountTarget() {
        return document.querySelector('#homeTab') ||
            document.querySelector('#indexPage .homeSectionsContainer') ||
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

    function getImageUrl(item) {
        var client = apiClient();
        var tag = item.ImageTags && item.ImageTags.Primary;

        if (client && typeof client.getImageUrl === 'function' && tag) {
            return client.getImageUrl(item.Id, {
                type: 'Primary',
                maxWidth: 300,
                quality: 90,
                tag: tag
            });
        }

        return null;
    }

    function navigateTo(itemId) {
        if (window.Dashboard && typeof window.Dashboard.navigate === 'function') {
            window.Dashboard.navigate('details?id=' + encodeURIComponent(itemId));
            return true;
        }
        return false;
    }

    function createCard(match) {
        var item = match.libraryItem;

        var card = document.createElement('a');
        card.className = 'jellytrends-card';
        card.href = '#/details?id=' + encodeURIComponent(item.Id);
        card.title = item.Name || '';
        card.addEventListener('click', function (event) {
            if (navigateTo(item.Id)) {
                event.preventDefault();
            }
        });

        var imageWrap = document.createElement('div');
        imageWrap.className = 'jellytrends-image-wrap';

        var imageUrl = getImageUrl(item);
        if (imageUrl) {
            var img = document.createElement('img');
            img.className = 'jellytrends-image';
            img.loading = 'lazy';
            img.src = imageUrl;
            img.alt = item.Name || '';
            imageWrap.appendChild(img);
        } else {
            var placeholder = document.createElement('div');
            placeholder.className = 'jellytrends-image jellytrends-placeholder';
            placeholder.textContent = item.Name || '';
            imageWrap.appendChild(placeholder);
        }

        var badge = document.createElement('span');
        badge.className = 'jellytrends-rank';
        badge.textContent = '#' + match.rank;
        imageWrap.appendChild(badge);

        var title = document.createElement('div');
        title.className = 'jellytrends-title';
        title.textContent = item.Name || '';

        card.appendChild(imageWrap);
        card.appendChild(title);
        return card;
    }

    function createSection(titleText, matches) {
        var section = document.createElement('section');
        section.className = 'jellytrends-section';

        var heading = document.createElement('h2');
        heading.className = 'jellytrends-heading';
        heading.textContent = titleText;
        section.appendChild(heading);

        var row = document.createElement('div');
        row.className = 'jellytrends-row';
        matches.forEach(function (match) {
            row.appendChild(createCard(match));
        });
        section.appendChild(row);

        return section;
    }

    function buildLookup(items) {
        var byTitle = new Map();
        var byTitleYear = new Map();
        var byProvider = new Map();

        function addProvider(prefix, value, item) {
            if (value === null || value === undefined || value === '') {
                return;
            }
            var key = prefix + ':' + String(value).toLowerCase();
            if (!byProvider.has(key)) {
                byProvider.set(key, item);
            }
        }

        (items || []).forEach(function (item) {
            [item.Name, item.OriginalTitle, item.SortName].forEach(function (raw) {
                var title = normalizeTitle(raw);
                if (!title) {
                    return;
                }
                if (!byTitle.has(title)) {
                    byTitle.set(title, []);
                }
                byTitle.get(title).push(item);
                if (item.ProductionYear) {
                    var yearKey = title + '|' + item.ProductionYear;
                    if (!byTitleYear.has(yearKey)) {
                        byTitleYear.set(yearKey, item);
                    }
                }
            });

            var ids = item.ProviderIds || {};
            Object.keys(ids).forEach(function (name) {
                addProvider(String(name).toLowerCase(), ids[name], item);
            });
        });

        return {
            byTitle: byTitle,
            byTitleYear: byTitleYear,
            byProvider: byProvider,
            allItems: items || []
        };
    }

    function selectBestCandidate(candidates, targetYear) {
        if (!candidates || !candidates.length) {
            return null;
        }
        if (!targetYear) {
            return candidates[0];
        }

        var best = null;
        var bestDiff = 999;
        candidates.forEach(function (candidate) {
            if (!candidate.ProductionYear) {
                return;
            }
            var diff = Math.abs(parseInt(candidate.ProductionYear, 10) - parseInt(targetYear, 10));
            if (diff < bestDiff) {
                bestDiff = diff;
                best = candidate;
            }
        });

        return (best && bestDiff <= 4) ? best : candidates[0];
    }

    function matchTrending(entries, lookup, options) {
        var matches = [];
        var used = new Set();

        (entries || []).some(function (entry) {
            if (matches.length >= options.maxItems) {
                return true;
            }

            var libraryItem =
                lookup.byProvider.get('imdb:' + String(entry.ImdbId || '').toLowerCase()) ||
                lookup.byProvider.get('tmdb:' + String(entry.TmdbId || '').toLowerCase()) ||
                lookup.byProvider.get('tvdb:' + String(entry.TvdbId || '').toLowerCase()) ||
                null;

            var key = normalizeTitle(entry.Title);
            if (!libraryItem && key && entry.Year) {
                libraryItem = lookup.byTitleYear.get(key + '|' + entry.Year) || null;
            }
            if (!libraryItem && key && !options.strictYearMatch) {
                libraryItem = selectBestCandidate(lookup.byTitle.get(key), entry.Year);
            }

            if (!libraryItem || used.has(libraryItem.Id)) {
                return false;
            }

            used.add(libraryItem.Id);
            matches.push({
                // The badge keeps the online chart position so a title sitting at #37
                // worldwide still reads '#37' even when it is the third hit in the library.
                rank: options.showOnlineRank ? entry.Rank : matches.length + 1,
                libraryItem: libraryItem
            });
            return false;
        });

        return matches;
    }

    function getItems(userId, includeType) {
        var client = apiClient();
        var query = {
            Recursive: true,
            IncludeItemTypes: includeType,
            // Only real ItemFields values are accepted; 10.11 rejects the whole request
            // with HTTP 400 otherwise. ProductionYear and ImageTags are always returned.
            Fields: 'ProviderIds,OriginalTitle,SortName',
            EnableImageTypes: 'Primary',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Limit: 50000
        };

        if (typeof client.getItems === 'function') {
            return client.getItems(userId, query).then(function (result) {
                return (result && result.Items) || [];
            });
        }

        return client.getJSON(client.getUrl('Users/' + userId + '/Items', query)).then(function (result) {
            return (result && result.Items) || [];
        });
    }

    function getItemsCached(userId, includeType) {
        var now = Date.now();
        if (state.libraryCache[includeType] && (now - state.libraryCacheAt[includeType]) < LIBRARY_CACHE_MS) {
            return Promise.resolve(state.libraryCache[includeType]);
        }

        return getItems(userId, includeType).then(function (items) {
            state.libraryCache[includeType] = items;
            state.libraryCacheAt[includeType] = Date.now();
            return items;
        });
    }

    function loadTrendingCached() {
        var client = apiClient();
        var now = Date.now();
        if (state.trendingCache && (now - state.trendingCacheAt) < TRENDING_CACHE_MS) {
            return Promise.resolve(state.trendingCache);
        }

        return client.getJSON(client.getUrl('JellyTrends/trending')).then(function (payload) {
            state.trendingCache = payload || {};
            state.trendingCacheAt = Date.now();
            return state.trendingCache;
        });
    }

    function loadConfig() {
        var client = apiClient();
        return client.getJSON(client.getUrl('JellyTrends/config'));
    }

    function mount(config, movieMatches, showMatches) {
        var target = getMountTarget();
        if (!target) {
            return false;
        }

        state.mounting = true;
        try {
            removeRoot();

            if (!movieMatches.length && !showMatches.length) {
                return true;
            }

            var root = document.createElement('div');
            root.id = ROOT_ID;
            root.className = 'jellytrends-root';
            root.style.setProperty('--jt-card-scale', String(clamp(config.CardScalePercent, 60, 180) / 100));
            root.style.setProperty('--jt-text-scale', String(clamp(config.TextScalePercent, 70, 180) / 100));

            var count = clamp(config.MaxDisplayItems, 1, 50);
            if (movieMatches.length) {
                root.appendChild(createSection('Top ' + count + ' Movies In Your Library', movieMatches));
            }
            if (showMatches.length) {
                root.appendChild(createSection('Top ' + count + ' Shows In Your Library', showMatches));
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

    function run() {
        if (state.busy || !apiClient() || !onHome() || isPlaybackActive()) {
            return;
        }

        var userId = getCurrentUserId();
        if (!userId) {
            return;
        }

        var runId = ++state.runId;
        state.busy = true;

        loadConfig().then(function (config) {
            if (!config || !config.Enabled) {
                state.rendered = null;
                removeRoot();
                return null;
            }

            return Promise.all([
                loadTrendingCached(),
                getItemsCached(userId, 'Movie'),
                getItemsCached(userId, 'Series')
            ]).then(function (results) {
                if (runId !== state.runId || !onHome() || isPlaybackActive()) {
                    return null;
                }

                var trending = results[0] || {};
                var options = {
                    maxItems: clamp(config.MaxDisplayItems, 1, 50),
                    strictYearMatch: !!config.StrictYearMatch,
                    showOnlineRank: config.ShowOnlineRank !== false
                };

                var movieMatches = matchTrending(trending.Movies, buildLookup(results[1]), options);
                var showMatches = matchTrending(trending.Shows, buildLookup(results[2]), options);

                state.config = config;
                state.rendered = { movies: movieMatches, shows: showMatches };
                mount(config, movieMatches, showMatches);
                return null;
            });
        }).catch(function (error) {
            if (window.console && console.warn) {
                console.warn('JellyTrends failed to render', error);
            }
        }).finally(function () {
            state.busy = false;
        });
    }

    /**
     * Re-attaches the rows without refetching when Jellyfin re-renders the home sections,
     * and tears them down as soon as the user leaves home or starts playback.
     */
    function ensure() {
        if (!onHome() || isPlaybackActive()) {
            removeRoot();
            return;
        }

        if (getRoot()) {
            return;
        }

        if (state.rendered && state.config) {
            if (mount(state.config, state.rendered.movies, state.rendered.shows)) {
                return;
            }
        }

        run();
    }

    function scheduleEnsure(delay) {
        // Throttle rather than debounce: a page that mutates continuously must still get
        // its rows back instead of resetting the timer forever.
        if (state.ensureTimer) {
            return;
        }
        state.ensureTimer = setTimeout(function () {
            state.ensureTimer = null;
            ensure();
        }, delay || 250);
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

        if (typeof MutationObserver === 'function') {
            new MutationObserver(function () {
                if (state.mounting) {
                    return;
                }
                scheduleEnsure(400);
            }).observe(document.body, { childList: true, subtree: true });
        }

        scheduleEnsure(1200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
