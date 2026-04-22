(function () {
    if (window.JellyTrendsInit) {
        return;
    }
    window.JellyTrendsInit = true;

    var state = {
        busy: false,
        lastHash: ''
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
        card.href = '#!/details?id=' + encodeURIComponent(item.Id);

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

        (items || []).forEach(function (item) {
            var title = normalizeTitle(item.Name);
            if (!title) {
                return;
            }

            if (!byTitle.has(title)) {
                byTitle.set(title, []);
            }
            byTitle.get(title).push(item);

            if (item.ProductionYear) {
                byTitleYear.set(title + '|' + item.ProductionYear, item);
            }
        });

        return {
            byTitle: byTitle,
            byTitleYear: byTitleYear
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
            if (entry.Year) {
                libraryItem = lookup.byTitleYear.get(key + '|' + entry.Year) || null;
            }

            if (!libraryItem && !strictYearMatch) {
                var candidates = lookup.byTitle.get(key) || [];
                libraryItem = candidates.length ? candidates[0] : null;
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

    function getItems(userId, includeType) {
        return window.ApiClient.getItems(userId, {
            Recursive: true,
            IncludeItemTypes: includeType,
            Fields: 'ProductionYear,ImageTags',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Limit: 50000
        }).then(function (result) {
            return (result && result.Items) ? result.Items : [];
        });
    }

    function loadPluginConfig() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl('JellyTrends/config'));
    }

    function loadTrending() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl('JellyTrends/trending'));
    }

    function getHomeTarget() {
        return document.querySelector('.homeSectionsContainer') || document.querySelector('.padded-bottom-page');
    }

    function render(matchesMovies, matchesShows) {
        var homeTarget = getHomeTarget();
        if (!homeTarget) {
            return;
        }

        var existing = document.querySelector('#jellytrends-root');
        if (existing) {
            existing.remove();
        }

        var root = document.createElement('div');
        root.id = 'jellytrends-root';
        root.className = 'jellytrends-root';

        root.appendChild(createSection('Top 10 Movies In Your Library', matchesMovies));
        root.appendChild(createSection('Top 10 Shows In Your Library', matchesShows));

        homeTarget.prepend(root);
    }

    function onHome() {
        return location.hash.indexOf('#!/home') === 0 ||
            location.hash.indexOf('#/home') === 0 ||
            location.hash.indexOf('#/home.html') === 0;
    }

    function run() {
        if (state.busy || !window.ApiClient || !onHome()) {
            return;
        }

        state.busy = true;

        var userId = getCurrentUserId();
        if (!userId) {
            state.busy = false;
            return;
        }

        Promise.all([
            loadPluginConfig(),
            loadTrending(),
            getItems(userId, 'Movie'),
            getItems(userId, 'Series')
        ]).then(function (all) {
            var config = all[0] || {};
            var trending = all[1] || {};
            var movies = all[2] || [];
            var shows = all[3] || [];

            if (!config.Enabled) {
                return;
            }

            var maxItems = Math.max(1, Math.min(25, parseInt(config.MaxDisplayItems, 10) || 10));
            var strictYearMatch = !!config.StrictYearMatch;

            var movieLookup = buildLookup(movies);
            var showLookup = buildLookup(shows);

            var movieMatches = matchTrending(trending.Movies || [], movieLookup, strictYearMatch, maxItems);
            var showMatches = matchTrending(trending.Shows || [], showLookup, strictYearMatch, maxItems);

            render(movieMatches, showMatches);
        }).catch(function (error) {
            if (console && console.warn) {
                console.warn('JellyTrends failed to render', error);
            }
        }).finally(function () {
            state.busy = false;
        });
    }

    function watchNavigation() {
        setInterval(function () {
            if (location.hash !== state.lastHash) {
                state.lastHash = location.hash;
                setTimeout(run, 250);
            }
        }, 500);
    }

    function init() {
        state.lastHash = location.hash;
        watchNavigation();
        setInterval(function () {
            if (onHome()) {
                run();
            }
        }, 30000);
        setTimeout(run, 1200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
