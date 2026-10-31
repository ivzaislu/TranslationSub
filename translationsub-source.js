(function () {
    'use strict';

    if (window.__TranslationSubSourceStarted) return;
    window.__TranslationSubSourceStarted = true;

    var SOURCE_SETTING = 'translationsub_card_source';
    var menuOpen = false;

    function storageGet(name, fallback) {
        try {
            if (window.Lampa && Lampa.Storage && typeof Lampa.Storage.get === 'function')
                return Lampa.Storage.get(name, fallback);
        } catch (e) {}

        try {
            var value = localStorage.getItem(name);
            return value === null ? fallback : value;
        } catch (e2) {
            return fallback;
        }
    }

    function selectedSource() {
        return String(storageGet(SOURCE_SETTING, 'tmdb') || 'tmdb').toLowerCase() === 'cub' ? 'cub' : 'tmdb';
    }

    function sourceTitle(source) {
        return source === 'cub' ? 'CUB' : 'TMDB';
    }

    function notify(text) {
        try {
            if (window.Lampa && Lampa.Noty && typeof Lampa.Noty.show === 'function')
                Lampa.Noty.show(text);
        } catch (e) {}
    }

    function openCard(item) {
        if (!window.Lampa || !Lampa.Activity || typeof Lampa.Activity.push !== 'function') return;

        var target = item && item.navigation && typeof item.navigation === 'object'
            ? item.navigation
            : {};
        var id = String(target.id || '').trim();
        if (!id) {
            notify('Карточка недоступна. Пересоздайте подписку из карточки сериала.');
            return;
        }

        var source = selectedSource();
        var numericId = /^\d+$/.test(id) ? Number(id) : id;
        var method = String(target.method || '').toLowerCase() === 'movie' ? 'movie' : 'tv';

        Lampa.Activity.push({
            url: '',
            component: 'full',
            source: source,
            id: numericId,
            method: method,
            card: { id: numericId, source: source }
        });
    }

    // Lampac's Online plugin accepts an explicit balancer through
    // lampac_custom_select and remembers season/voice in online_choice_<id>.
    // Season is an index there, not a season number: this is a best-effort
    // integration for standard season lists; the user can correct it in Online.
    function latestEpisodeSources(item) {
        var episode = Number(item && (item.toEpisode || item.availableEpisode)) || 0;
        if (episode <= 0) return [];

        var seen = {};
        return (Array.isArray(item && item.sources) ? item.sources : []).filter(function (row) {
            if (!row || Number(row.latestEpisode) < episode) return false;
            var id = String(row.source || '').toLowerCase().trim();
            if (!/^[a-z0-9][a-z0-9._-]{0,119}$/.test(id) || seen[id]) return false;
            seen[id] = true;
            return true;
        });
    }

    // Lampac creates its content controller before the asynchronous Online
    // episode list has any selectable rows. On a TV its first collectionFocus
    // can therefore select nothing. Restore focus *once* when real rows arrive,
    // and only while this exact Online activity still owns the content view.
    function focusOnlineAfterLoad(onlineActivity) {
        if (!window.Lampa || !Lampa.Platform || typeof Lampa.Platform.screen !== 'function'
            || !Lampa.Platform.screen('tv')
            || !Lampa.Controller || typeof Lampa.Controller.toggle !== 'function'
            || typeof Lampa.Controller.enabled !== 'function'
            || !Lampa.Activity || typeof Lampa.Activity.active !== 'function')
            return;

        var attempts = 0;
        function checkFocus() {
            try {
                // Never steal the remote from a new screen, menu, filter or
                // player opened by the user while network requests complete.
                if (Lampa.Activity.active() !== onlineActivity) return;
                var enabled = Lampa.Controller.enabled();
                if (!enabled || enabled.name !== 'content') return;

                var view = onlineActivity.activity && onlineActivity.activity.render
                    ? onlineActivity.activity.render() : null;
                if (view && typeof view.find === 'function') {
                    if (view.find('.selector.focus').length) return;

                    // The header/poster exists before any Online results.
                    // Wait for actual playable rows rather than focusing early.
                    if (view.find('.online-prestige--full.selector, .online-prestige--folder.selector').length) {
                        // Forced toggle avoids a stale windowRedirect after
                        // closing the notification modal on TV.
                        Lampa.Controller.toggle('content', true);
                        return;
                    }
                }
            } catch (e) {
                return;
            }

            if (++attempts < 60) setTimeout(checkFocus, 250);
        }
        setTimeout(checkFocus, 250);
    }

    function openOnBalancer(item, entry, hooks) {
        if (!window.lampac_plugin || !window.Lampa || !Lampa.Activity
            || typeof Lampa.Activity.push !== 'function'
            || !Lampa.Storage || typeof Lampa.Storage.cache !== 'function'
            || typeof Lampa.Storage.set !== 'function') return false;

        var navigation = item && item.navigation && typeof item.navigation === 'object' ? item.navigation : {};
        var tmdbId = String(navigation.id || item.tmdbId || '').trim();
        var season = Math.floor(Number(item && item.season));
        var voice = String(entry && (entry.translationName || item.translationName) || '').trim();
        var balancer = String(entry && entry.source || '').toLowerCase().trim();
        if (!/^\d+$/.test(tmdbId) || !Number.isSafeInteger(Number(tmdbId))
            || !Number.isInteger(season) || season < 1 || !voice || !balancer)
            return false;

        var id = Number(tmdbId);
        var title = String(item.title || item.originalTitle || '').trim();
        var original = String(item.originalTitle || title).trim();
        var movie = {
            id: id,
            tmdb_id: id,
            source: 'tmdb',
            name: title,
            title: title,
            original_name: original,
            original_title: original,
            number_of_seasons: season,
            poster_path: item.poster || '',
            imdb_id: item.imdbId || '',
            kinopoisk_id: item.kpId || '',
            // Lampac reads the year from the first four characters.
            // Do not fabricate an actual premiere date.
            first_air_date: item.year ? String(item.year) : ''
        };

        try {
            var key = 'online_choice_' + balancer;
            var choices = Lampa.Storage.cache(key, 3000, {}) || {};
            var previous = choices[id] && typeof choices[id] === 'object' ? choices[id] : {};
            choices[id] = Object.assign({}, previous, {
                season: Math.max(0, season - 1),
                voice: 0,
                voice_name: voice,
                voice_id: entry.translationId || item.translationId || 0,
                voice_url: ''
            });
            Lampa.Storage.set(key, choices);

            var activity = {
                url: '',
                title: 'Онлайн · ' + title,
                component: 'lampac',
                movie: movie,
                search: title,
                search_one: title,
                search_two: original,
                page: 1,
                clarification: false,
                lampac_custom_select: balancer
            };

            // The stock Lampa Explorer poster acts as Activity.back() when
            // there is a previous activity. Insert the series card *only* for
            // this opt-in notification -> Online route, immediately underneath
            // Lampac. OK on the poster then reveals the series card; regular
            // navigation and the disabled setting remain unchanged.
            var seriesCard = {
                url: '',
                component: 'full',
                source: 'tmdb',
                id: id,
                method: 'tv',
                card: movie
            };

            // Close the notice modal before handing TV remote focus to the
            // activity stack. Both pushes are synchronous and ordered.
            if (hooks && typeof hooks.beforeNavigate === 'function')
                hooks.beforeNavigate();
            setTimeout(function () {
                var cardAdded = false;
                try {
                    Lampa.Activity.push(seriesCard);
                    cardAdded = true;
                    Lampa.Activity.push(activity);
                    focusOnlineAfterLoad(activity);
                } catch (e) {
                    // If Online fails after inserting the card, leave that
                    // already-open card visible (no duplicate fallback push).
                    if (!cardAdded && hooks && typeof hooks.onFailure === 'function')
                        hooks.onFailure();
                }
            }, 0);
            return true;
        } catch (e) {
            return false;
        }
    }

    function openNoticeOnline(item, hooks) {
        var candidates = latestEpisodeSources(item);
        if (!candidates.length || !window.lampac_plugin) return false;

        if (candidates.length === 1) return openOnBalancer(item, candidates[0], hooks);
        if (!window.Lampa || !Lampa.Select || typeof Lampa.Select.show !== 'function')
            return false;

        try {
            Lampa.Select.show({
                title: 'Балансер для просмотра',
                items: candidates.map(function (entry) {
                    var id = String(entry.source || '');
                    return {
                        title: id.charAt(0).toUpperCase() + id.slice(1),
                        subtitle: 'S' + item.season + ' · ' + String(entry.translationName || item.translationName || ''),
                        sourceEntry: entry
                    };
                }),
                onSelect: function (chosen) {
                    // Lampa Select automatically closes itself on OK, *before*
                    // calling onSelect. A second Select.close() closes the
                    // underlying modal or steals focus on television.
                    if (!openOnBalancer(item, chosen && chosen.sourceEntry, hooks)) {
                        if (hooks && typeof hooks.onFailure === 'function')
                            hooks.onFailure();
                        else openCard(item);
                    }
                }
                // No onBack handler: Lampa's window stack returns to the
                // original focused notification when the remote presses Back.
            });
            return true;
        } catch (e) {
            return false;
        }
    }

    function refreshState(result) {
        try {
            var badge = window.TranslationSubBadgeState;
            if (badge && typeof badge.applyCommand === 'function' && badge.applyCommand(result)) return;
            if (badge && typeof badge.refresh === 'function') badge.refresh();
        } catch (e) {}
    }

    function runAction(action, previousController) {
        window.TranslationSubRuntime.restoreController(previousController, 'content');
        if (!action || typeof action.onclick !== 'function') return;
        setTimeout(function () {
            try { action.onclick(); } catch (e) {}
        }, 0);
    }

    function showActions(item) {
        if (menuOpen) return;
        if (!window.Lampa || !Lampa.Select || typeof Lampa.Select.show !== 'function') return;

        item = item || {};
        var display = item.display && typeof item.display === 'object' ? item.display : {};
        var id = String(item.id || '');
        var title = String(item.title || 'Подписка');
        var voice = String(item.translationName || 'Озвучка');
        var previousController = window.TranslationSubRuntime.controllerName('content');
        var actions = [];

        if (display.newBadge) {
            actions.push({
                title: String(display.progress || display.newBadge),
                subtitle: String(display.noticeRange || ''),
                onclick: function () { openCard(item); }
            });
        }

        actions.push({
            title: 'Открыть карточку · ' + sourceTitle(selectedSource()),
            onclick: function () { openCard(item); }
        });

        actions.push({
            title: 'Отписаться от озвучки',
            subtitle: voice + (display.season ? (' · ' + String(display.season)) : ''),
            onclick: function () {
                if (!id) return;
                var api = window.TranslationSubApi;
                if (!api || typeof api.unsubscribe !== 'function') {
                    notify('TranslationSub API недоступен');
                    return;
                }

                api.unsubscribe(id, function (result) {
                    if (!result || result.success !== true) {
                        notify('Не удалось отписаться · ' + voice);
                        refreshState(result);
                        return;
                    }
                    notify('Вы отписались · ' + voice + (display.season ? (' · ' + String(display.season)) : ''));
                    refreshState(result);
                }, function () {
                    notify('Не удалось отписаться · ' + voice);
                });
            }
        });

        menuOpen = true;
        try {
            Lampa.Select.show({
                title: title,
                items: actions,
                onSelect: function (action) {
                    menuOpen = false;
                    runAction(action, previousController);
                },
                onBack: function () {
                    menuOpen = false;
                    window.TranslationSubRuntime.restoreController(previousController, 'content');
                }
            });
        } catch (e) {
            menuOpen = false;
            window.TranslationSubRuntime.restoreController(previousController, 'content');
        }
    }

    function bindPage(root, list) {
        if (typeof $ !== 'function') return;
        root = root && root.jquery ? root : $(root);
        list = Array.isArray(list) ? list : [];

        var byId = {};
        list.forEach(function (item) {
            var id = String(item && item.id || '');
            if (id) byId[id] = item;
        });

        root.find('.translationsub-card').each(function () {
            var card = $(this);
            var id = String(card.attr('data-subscription-id') || '');
            var item = byId[id];
            if (!item) return;

            card.off('.translationsubSource');
            card.on('hover:enter.translationsubSource', function () { openCard(item); });
            card.on('hover:long.translationsubSource', function (event) {
                try { if (event && typeof event.stopPropagation === 'function') event.stopPropagation(); } catch (e) {}
                showActions(item);
            });
        });
    }

    function start() {
        window.TranslationSubCardSource = {
            open: openCard,
            openNoticeOnline: openNoticeOnline,
            bindPage: bindPage
        };
    }

    window.TranslationSubRuntime.onReady(start);
})();
