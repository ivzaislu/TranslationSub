(function () {
    'use strict';

    if (window.__TranslationSubSourceStarted) return;
    window.__TranslationSubSourceStarted = true;

    var SOURCE_SETTING = 'translationsub_card_source';
    var menuOpen = false;
    var focusListenerInstalled = false;
    var focusRecoverySeq = 0;

    // Opt-in tracing for the intermittent Lampa keyboard/remote focus loss.
    // This does NOT repair or change navigation: it only records which core
    // method last changed the controller or its Navigator collection.
    // Enable from the browser console: TranslationSubFocusTrace.start()
    // Reproduce twice, then: copy(TranslationSubFocusTrace.report())
    function createFocusTrace() {
        var active = false;
        var records = [];
        var restorers = [];
        var startedAt = 0;
        var sampler = null;
        var lastState = '';
        var activityListener = null;
        var keyListener = null;
        var MAX = 400;

        function state() {
            var result = { activity: '', controller: '', window: '', rows: 0,
                rowFocus: 0, focus: 0, collection: null };
            try {
                var a = Lampa.Activity && Lampa.Activity.active && Lampa.Activity.active();
                result.activity = a && a.component || '';
                if (a && a.activity && typeof a.activity.render === 'function') {
                    var view = a.activity.render();
                    if (view && typeof view.find === 'function') {
                        result.rows = view.find('.online-prestige--full.selector, .online-prestige--folder.selector').length;
                        result.rowFocus = view.find('.online-prestige--full.selector.focus, .online-prestige--folder.selector.focus').length;
                        result.focus = view.find('.selector.focus').length;
                    }
                }
                var c = Lampa.Controller && Lampa.Controller.enabled && Lampa.Controller.enabled();
                result.controller = c && c.name || '';
                var w = Lampa.Controller && Lampa.Controller.windowTop && Lampa.Controller.windowTop();
                result.window = w && w.name || '';
                var nav = window.Navigator || (Lampa && Lampa.Navigator);
                if (nav && nav._collection) result.collection = nav._collection.length;
            } catch (e) {}
            return result;
        }

        function push(type, detail, before, after, caller) {
            if (!active) return;
            records.push({
                ms: Date.now() - startedAt,
                event: type,
                detail: detail || '',
                before: before || null,
                after: after || state(),
                caller: caller || ''
            });
            if (records.length > MAX) records.shift();
        }

        function caller() {
            try {
                var stack = String((new Error()).stack || '').split('\n').slice(3, 7);
                return stack.map(function (line) {
                    // Keep only source filenames and line numbers: never log
                    // document URLs, query strings, movie names, or tokens.
                    var match = line.match(/([A-Za-z0-9_.-]+\\.js):(\\d+):(\\d+)/);
                    return match ? match[1] + ':' + match[2] : 'unknown';
                }).join(' > ');
            } catch (e) { return ''; }
        }

        function details(label, args) {
            if (label === 'Controller.add' || label === 'Controller.toggle'
                || label === 'Controller.move' || label === 'Controller.trigger')
                return String(args[0] || '').slice(0, 50);
            if (label === 'Activity.push') return String(args[0] && args[0].component || '');
            if (label === 'Activity.replace') return 'activity-replaced';
            if (label === 'Controller.collectionSet') return 'set-collection';
            if (label === 'Controller.collectionFocus') return 'focus-element';
            return '';
        }

        function instrument(object, name, label) {
            if (!object || typeof object[name] !== 'function') return;
            var original = object[name];
            var patched = function () {
                var args = Array.prototype.slice.call(arguments);
                var before = state();
                var origin = caller();
                try {
                    return original.apply(this, args);
                } finally {
                    push(label, details(label, args), before, state(), origin);
                }
            };
            object[name] = patched;
            restorers.push(function () {
                if (object[name] === patched) object[name] = original;
            });
        }

        function recordState() {
            if (!active) return;
            var now = state();
            var signature = JSON.stringify(now);
            if (signature !== lastState) {
                lastState = signature;
                push('state-change', '', null, now, '');
            }
        }

        function startTrace() {
            if (active) return 'Focus trace is already running';
            if (!window.Lampa || !Lampa.Controller || !Lampa.Activity)
                return 'Lampa is not ready';
            records = [];
            startedAt = Date.now();
            lastState = '';
            active = true;

            ['add', 'toggle', 'clear', 'collectionSet', 'collectionFocus',
                'collectionAppend', 'focus', 'move', 'enter', 'back'].forEach(function (name) {
                instrument(Lampa.Controller, name, 'Controller.' + name);
            });
            ['push', 'replace', 'back', 'backward'].forEach(function (name) {
                instrument(Lampa.Activity, name, 'Activity.' + name);
            });

            if (Lampa.Listener && typeof Lampa.Listener.follow === 'function') {
                activityListener = function (event) {
                    if (event && event.component)
                        push('activity-event', String(event.type || '') + ':' +
                            String(event.component).slice(0, 30));
                };
                Lampa.Listener.follow('activity', activityListener);
            }

            if (typeof document !== 'undefined' && document.addEventListener) {
                keyListener = function (event) {
                    var code = event.keyCode || event.which;
                    if ([13, 27, 37, 38, 39, 40, 8].indexOf(code) !== -1)
                        push('keydown', String(code));
                };
                document.addEventListener('keydown', keyListener, true);
            }

            recordState();
            if (typeof setInterval === 'function') sampler = setInterval(recordState, 300);
            return 'Focus trace recording. Reproduce the failure, then copy(TranslationSubFocusTrace.report()).';
        }

        function stopTrace() {
            if (!active) return 'Focus trace stopped';
            push('trace-stop');
            active = false;
            if (sampler) clearInterval(sampler);
            sampler = null;
            while (restorers.length) restorers.pop()();
            if (activityListener && Lampa.Listener && Lampa.Listener.remove)
                Lampa.Listener.remove('activity', activityListener);
            activityListener = null;
            if (keyListener && typeof document !== 'undefined' && document.removeEventListener)
                document.removeEventListener('keydown', keyListener, true);
            keyListener = null;
            return 'Focus trace stopped; report() still works';
        }

        return {
            version: 'focus-diagnostic-1',
            start: startTrace,
            stop: stopTrace,
            clear: function () { records = []; lastState = ''; },
            status: function () {
                return { active: active, events: records.length, version: 'focus-diagnostic-1' };
            },
            report: function () {
                if (active) recordState();
                return JSON.stringify({
                    version: 'focus-diagnostic-1', active: active,
                    current: state(), events: records
                }, null, 2);
            }
        };
    }

    window.TranslationSubFocusTrace = createFocusTrace();

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

    // Reattach remote/keyboard navigation to the *current* Lampac activity.
    // A .focus class alone is not reliable: Lampa keeps the active controller,
    // Navigator collection and selected node separately. Lampac can also clear
    // and redraw its episode list after the first network response.
    function focusOnlineAfterLoad(onlineActivity) {
        if (!window.Lampa || !Lampa.Controller
            || typeof Lampa.Controller.toggle !== 'function'
            || typeof Lampa.Controller.enabled !== 'function'
            || !Lampa.Activity || typeof Lampa.Activity.active !== 'function')
            return;

        var sequence = ++focusRecoverySeq;
        var attempts = 0;
        var focusedRows = null;
        var stableFrames = 0;

        function checkFocus() {
            if (sequence !== focusRecoverySeq) return;

            try {
                if (Lampa.Activity.active() !== onlineActivity) return;

                var enabled = Lampa.Controller.enabled();
                if (!enabled) return;

                // Never interrupt a real menu, player, modal, or user-chosen
                // navigation area. Legacy full-card controllers can be left
                // active by asynchronous navigation and are safe to replace.
                if (enabled.name !== 'content'
                    && enabled.name !== 'full_start'
                    && enabled.name !== 'full_descr'
                    && enabled.name !== 'items_line') return;
                if (typeof Lampa.Controller.windowTop === 'function'
                    && Lampa.Controller.windowTop()) return;

                var slide = onlineActivity.activity;
                var view = slide && typeof slide.render === 'function' ? slide.render() : null;
                if (view && typeof view.find === 'function') {
                    var rows = view.find('.online-prestige--full.selector, .online-prestige--folder.selector');
                    var row = rows && rows.length ? rows[0] : null;

                    // Do not focus an element while the slide is hidden.
                    if (row && row.offsetParent !== null) {
                        var changed = !focusedRows || focusedRows !== row;
                        if (changed) {
                            // Re-register the *Lampac* content controller.
                            // A blind Controller.toggle('content') can enable
                            // the previous screen's controller after repeated
                            // navigations and leave the keyboard unusable.
                            var component = slide.component;
                            if (component && typeof component.start === 'function')
                                component.start();

                            Lampa.Controller.toggle('content', true);

                            // Lampa's native focus call updates select_active
                            // (used by OK), not just the CSS .focus class.
                            var focused = view.find('.online-prestige--full.selector.focus, .online-prestige--folder.selector.focus');
                            var target = focused && focused.length ? focused[0] : row;
                            if (typeof Lampa.Controller.collectionFocus === 'function')
                                Lampa.Controller.collectionFocus(target, view);

                            focusedRows = row;
                            stableFrames = 0;
                        } else {
                            stableFrames++;
                        }
                    }
                }
            } catch (e) {
                // The Online component may still be building its view.
                // Retry while the activity is alive instead of aborting.
            }

            // Keep watching briefly after first focus: async requests may
            // replace the episode nodes and invalidate Navigator's collection.
            if (++attempts < 180 && (!focusedRows || stableFrames < 20))
                setTimeout(checkFocus, 500);
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
                lampac_custom_select: balancer,
                translationsub_direct_notice: true
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
                    // Lampa emits the activity start event after each push.
                    // Fallback covers older builds without that listener.
                    if (!focusListenerInstalled) focusOnlineAfterLoad(activity);
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

        // The Online plugin replaces the whole activity when switching
        // balancers. A monitor tied only to the first object silently dies.
        // Keep the flag on the activity so every replacement can be focused
        // independently; ordinary Online routes are never intercepted.
        if (Lampa.Listener && typeof Lampa.Listener.follow === 'function') {
            Lampa.Listener.follow('activity', function (event) {
                if (!event || event.type !== 'start' || event.component !== 'lampac'
                    || !event.object || event.object.translationsub_direct_notice !== true)
                    return;
                focusOnlineAfterLoad(event.object);
            });
            focusListenerInstalled = true;
        }
    }

    window.TranslationSubRuntime.onReady(start);
})();
