(function () {
    if (window.JellyTrendsInit) {
        return;
    }
    window.JellyTrendsInit = true;

    var state = {
        busy: false,
        lastHash: '',
        runId: 0,
        trendingCache: null,
        trendingCacheAt: 0,
        moviesCache: null,
        moviesCacheAt: 0,
        showsCache: null,
        showsCacheAt: 0
    };

    var TRENDING_CACHE_MS = 5 * 60 * 1000;
    var LIBRARY_CACHE_MS = 15 * 60 * 1000;

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

    function getCurrentUserId() {
        if (window.ApiClient && typeof window.ApiClient.getCurrentUserId === 'function') {
            return window.ApiClient.getCurrentUserId();
        }
        if (window.ApiClient && window.ApiClient._serverInfo && window.ApiClient._serverInfo.UserId) {
            return window.ApiClient._serverInfo.UserId;
        }
        return null;
    }

    function getImageUrl(item) {
        if (item.ImageTags && item.ImageTags.Primary) {
            return '/Items/' + item.Id + '/Images/Primary?fillHeight=330&maxWidth=220&quality=90&tag=' + encodeURIComponent(item.ImageTags.Primary);
        }
        return '/web/assets/img/card.png';
    }

    function createCard(item, rank) {
        var card = document.createElement('a');
        card.className = 'jellytrends-card';
        card.href = '#/details?id=' + encodeURIComponent(item.Id);
        card.addEventListener('click', function (event) {
            if (window.Dashboard && typeof window.Dashboard.navigate === 'function') {
                event.preventDefault();
                window.Dashboard.navigate('details?id=' + encodeURIComponent(item.Id));
            }
        });

        var imageWrap = document.createElement('div');
        imageWrap.className = 'jellytrends-image-wrap';

        var img = document.createElement('img');
        img.className = 'jellytrends-image';
        img.loading = 'lazy';
        img.src = getImageUrl(item);
        img.alt = item.Name || 'Library item';

        var badge = document.createElement('span');
        badge.className = 'jellytrends-rank';
        badge.textContent = '#' + rank;

        var title = document.createElement('div');
        title.className = 'jellytrends-title';
        title.textContent = item.Name || 'Unknown';

        imageWrap.appendChild(img);
        imageWrap.appendChild(badge);
        card.appendChild(imageWrap);
        card.appendChild(title);
        return card;
    }

    function createSection(titleText, items) {
        var section = document.createElement('section');
        section.className = 'jellytrends-section';

        var title = document.createElement('h2');
        title.className = 'jellytrends-heading';
        title.textContent = titleText;
        section.appendChild(title);

        if (!items || !items.length) {
            var empty = document.createElement('div');
            empty.className = 'jellytrends-empty';
            empty.textContent = 'No matching titles found in your library yet.';
            section.appendChild(empty);
            return section;
        }

        var row = document.createElement('div');
        row.className = 'jellytrends-row';

        items.forEach(function (item) {
            row.appendChild(createCard(item.libraryItem, item.rank));
        });

        section.appendChild(row);
        return section;
    }

    function buildLookup(items) {
        var byTitle = new Map();
        var byTitleYear = new Map();
        var byImdb = new Map();
        var byTmdb = new Map();
        var byTvdb = new Map();

        (items || []).forEach(function (item) {
            var variants = [item.Name, item.OriginalTitle, item.SortName]
                .map(function (x) { return normalizeTitle(x); })
                .filter(function (x) { return !!x; });

            variants.forEach(function (title) {
                if (!byTitle.has(title)) {
                    byTitle.set(title, []);
                }
                byTitle.get(title).push(item);

                if (item.ProductionYear) {
                    byTitleYear.set(title + '|' + item.ProductionYear, item);
                }
            });

            var providerIds = item.ProviderIds || {};
            var imdb = providerIds.Imdb || providerIds.imdb || providerIds.IMDB || null;
            var tmdb = providerIds.Tmdb || providerIds.TMDb || providerIds.TheMovieDb || providerIds.themoviedb || null;
            var tvdb = providerIds.Tvdb || providerIds.TVDB || providerIds.thetvdb || null;
            if (imdb) {
                byImdb.set(String(imdb).toLowerCase(), item);
            }
            if (tmdb) {
                byTmdb.set(String(tmdb), item);
            }
            if (tvdb) {
                byTvdb.set(String(tvdb), item);
            }
        });

        return {
            byTitle: byTitle,
            byTitleYear: byTitleYear,
            byImdb: byImdb,
            byTmdb: byTmdb,
            byTvdb: byTvdb
        };
    }

    function matchTrending(trending, lookup, strictYearMatch, maxItems) {
        var matches = [];
        var used = new Set();

        (trending || []).forEach(function (entry) {
            if (matches.length >= maxItems) {
                return;
            }

            var key = normalizeTitle(entry.Title);
            if (!key) {
                return;
            }

            var libraryItem = null;
            if (entry.ImdbId) {
                libraryItem = lookup.byImdb.get(String(entry.ImdbId).toLowerCase()) || null;
            }
            if (!libraryItem && entry.TmdbId) {
                libraryItem = lookup.byTmdb.get(String(entry.TmdbId)) || null;
            }
            if (!libraryItem && entry.TvdbId) {
                libraryItem = lookup.byTvdb.get(String(entry.TvdbId)) || null;
            }

            if (entry.Year) {
                libraryItem = libraryItem || lookup.byTitleYear.get(key + '|' + entry.Year) || null;
            }

            if (!libraryItem && !strictYearMatch) {
                var candidates = lookup.byTitle.get(key) || [];
                libraryItem = selectBestCandidate(candidates, entry.Year);
            }

            if (!libraryItem || used.has(libraryItem.Id)) {
                return;
            }

            used.add(libraryItem.Id);
            matches.push({
                rank: entry.Rank,
                libraryItem: libraryItem
            });
        });

        return matches;
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
            var year = candidate.ProductionYear || null;
            if (!year) {
                return;
            }

            var diff = Math.abs(parseInt(year, 10) - parseInt(targetYear, 10));
            if (diff < bestDiff) {
                bestDiff = diff;
                best = candidate;
            }
        });

        if (best && bestDiff <= 4) {
            return best;
        }

        return candidates[0] || null;
    }

    function getItems(userId, includeType) {
        var query = {
            Recursive: true,
            IncludeItemTypes: includeType,
            Fields: 'ProductionYear,ImageTags,ProviderIds,OriginalTitle',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Limit: 50000
        };

        if (window.ApiClient && typeof window.ApiClient.getItems === 'function') {
            return window.ApiClient.getItems(userId, query)
                .then(function (result) {
                    return (result && result.Items) ? result.Items : [];
                })
                .catch(function () {
                    return window.ApiClient.getJSON(window.ApiClient.getUrl('Users/' + userId + '/Items', query))
                        .then(function (result) {
                            return (result && result.Items) ? result.Items : [];
                        });
                });
        }

        return window.ApiClient.getJSON(window.ApiClient.getUrl('Users/' + userId + '/Items', query))
            .then(function (result) {
                return (result && result.Items) ? result.Items : [];
            });
    }

    function loadPluginConfig() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl('JellyTrends/config'));
    }

    function loadTrending() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl('JellyTrends/trending'));
    }

    function loadTrendingCached() {
        var now = Date.now();
        if (state.trendingCache && (now - state.trendingCacheAt) < TRENDING_CACHE_MS) {
            return Promise.resolve(state.trendingCache);
        }

        return loadTrending().then(function (payload) {
            state.trendingCache = payload || {};
            state.trendingCacheAt = Date.now();
            return state.trendingCache;
        });
    }

    function getItemsCached(userId, includeType) {
        var now = Date.now();
        if (includeType === 'Movie' && state.moviesCache && (now - state.moviesCacheAt) < LIBRARY_CACHE_MS) {
            return Promise.resolve(state.moviesCache);
        }
        if (includeType === 'Series' && state.showsCache && (now - state.showsCacheAt) < LIBRARY_CACHE_MS) {
            return Promise.resolve(state.showsCache);
        }

        return getItems(userId, includeType).then(function (items) {
            if (includeType === 'Movie') {
                state.moviesCache = items;
                state.moviesCacheAt = Date.now();
            } else if (includeType === 'Series') {
                state.showsCache = items;
                state.showsCacheAt = Date.now();
            }

            return items;
        });
    }

    function getHomeTarget() {
        return document.querySelector('.homeSectionsContainer') ||
            document.querySelector('#homePage .padded-bottom-page') ||
            document.querySelector('.mainAnimatedPage[data-type="home"] .padded-bottom-page');
    }

    function removeRoot() {
        var existing = document.querySelector('#jellytrends-root');
        if (existing) {
            existing.remove();
        }
    }

    function render(matchesMovies, matchesShows) {
        if (!onHome()) {
            return;
        }

        var homeTarget = getHomeTarget();
        if (!homeTarget) {
            return;
        }

        removeRoot();

        var root = document.createElement('div');
        root.id = 'jellytrends-root';
        root.className = 'jellytrends-root';

        root.appendChild(createSection('Top 10 Movies In Your Library', matchesMovies));
        root.appendChild(createSection('Top 10 Shows In Your Library', matchesShows));

        homeTarget.prepend(root);
    }

    function applySizing(config) {
        var root = document.querySelector('#jellytrends-root');
        if (!root) {
            return;
        }

        var cardScale = Math.max(60, Math.min(180, parseInt(config.CardScalePercent, 10) || 100)) / 100;
        var textScale = Math.max(70, Math.min(180, parseInt(config.TextScalePercent, 10) || 100)) / 100;
        root.style.setProperty('--jt-card-scale', String(cardScale));
        root.style.setProperty('--jt-text-scale', String(textScale));
    }

    function onHome() {
        return location.hash.indexOf('#!/home') === 0 ||
            location.hash.indexOf('#/home') === 0 ||
            location.hash.indexOf('#/home.html') === 0;
    }

    function isPlaybackActive() {
        return !!(
            document.querySelector('.videoPlayerContainer:not(.hide)') ||
            document.querySelector('.htmlVideoPlayerContainer:not(.hide)') ||
            document.querySelector('.videoOsd:not(.hide)')
        );
    }

    function run() {
        if (state.busy || !window.ApiClient || !onHome() || isPlaybackActive()) {
            if (!onHome() || isPlaybackActive()) {
                removeRoot();
            }
            return;
        }

        var currentRunId = ++state.runId;
        state.busy = true;

        var userId = getCurrentUserId();
        if (!userId) {
            state.busy = false;
            return;
        }

        Promise.all([
            loadPluginConfig(),
            loadTrendingCached(),
            getItemsCached(userId, 'Movie'),
            getItemsCached(userId, 'Series')
        ]).then(function (all) {
            if (currentRunId !== state.runId || !onHome() || isPlaybackActive()) {
                removeRoot();
                return;
            }

            var config = all[0] || {};
            var trending = all[1] || {};
            var movies = all[2] || [];
            var shows = all[3] || [];

            if (!config.Enabled) {
                return;
            }

            var maxItems = Math.max(1, Math.min(100, parseInt(config.MaxDisplayItems, 10) || 10));
            var strictYearMatch = !!config.StrictYearMatch;

            var movieLookup = buildLookup(movies);
            var showLookup = buildLookup(shows);

            var movieMatches = matchTrending(trending.Movies || [], movieLookup, strictYearMatch, maxItems);
            var showMatches = matchTrending(trending.Shows || [], showLookup, strictYearMatch, maxItems);

            render(movieMatches, showMatches);
            applySizing(config);
        }).catch(function (error) {
            if (console && console.warn) {
                console.warn('JellyTrends failed to render', error);
            }
        }).finally(function () {
            state.busy = false;
        });
    }

    function handleNavigationChange() {
        state.lastHash = location.hash;
        state.runId++;

        if (!onHome() || isPlaybackActive()) {
            removeRoot();
            return;
        }

        setTimeout(run, 250);
    }

    function init() {
        state.lastHash = location.hash;
        window.addEventListener('hashchange', handleNavigationChange, true);
        window.addEventListener('popstate', handleNavigationChange, true);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                handleNavigationChange();
            }
        });
        setTimeout(run, 1200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
