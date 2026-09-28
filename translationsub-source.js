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
            bindPage: bindPage
        };
    }

    window.TranslationSubRuntime.onReady(start);
})();
