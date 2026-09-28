(function () {
    'use strict';

    if (window.__TranslationSubCardFlowStarted) return;
    window.__TranslationSubCardFlowStarted = true;

    var lastEvent = null;
    var applyTimer = null;
    var viewToken = 0;
    var dialogToken = 0;
    var busy = false;

    function notify(text) {
        try {
            if (window.Lampa && Lampa.Noty && typeof Lampa.Noty.show === 'function') {
                Lampa.Noty.show(text);
                return;
            }
        } catch (e) {}
        try { console.log('[TranslationSub]', text); } catch (e2) {}
    }

    function bellSvg() {
        return '<svg class="translationsub-full-button__bell" viewBox="0 0 24 24" aria-hidden="true"></svg>';
    }

    function primitive(value) {
        return value === null || value === undefined ||
            typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';
    }

    function copyFields(source, names) {
        var target = {};
        source = source || {};
        names.forEach(function (name) {
            var value = source[name];
            if (primitive(value)) target[name] = value;
        });
        return target;
    }

    function safeNested(value) {
        if (!value || typeof value !== 'object') return value;
        try { return JSON.parse(JSON.stringify(value)); }
        catch (e) { return null; }
    }

    function transportPayload(object) {
        object = object || {};
        var card = object.movie || object.card || object.data || object || {};

        var root = copyFields(object, [
            'method', 'media_type', 'type', 'serial', 'is_serial', 'isSerial',
            'season', 'source', 'id', 'title'
        ]);

        var rawCard = copyFields(card, [
            'id', 'source', 'card_source', 'tmdb_id', 'tmdbId',
            'kinopoisk_id', 'kp_id', 'kpId', 'imdb_id', 'imdbId',
            'title', 'name', 'original_title', 'original_name',
            'first_air_date', 'release_date', 'last_air_date', 'year',
            'poster_path', 'poster', 'img', 'image', 'content_id', 'contentId',
            'serial', 'is_serial', 'isSerial', 'season', 'media_type', 'type',
            'method', 'number_of_seasons', 'seasons_count',
            'number_of_episodes', 'episodes_count'
        ]);

        ['external_ids', 'last_episode_to_air', 'next_episode_to_air', 'episode_run_time', 'seasons']
            .forEach(function (name) {
                if (card[name] === undefined || card[name] === null) return;
                var value = safeNested(card[name]);
                if (value !== null) rawCard[name] = value;
            });

        root.card = rawCard;
        return root;
    }

    function activeFull() {
        try { return typeof $ === 'function' && $('.full-start:visible,.full-start-new:visible').length > 0; }
        catch (e) { return false; }
    }

    function showSelect(title, items, token, previousController) {
        items = Array.isArray(items) ? items : [];
        if (token !== dialogToken || !activeFull()) return;
        if (!items.length) return notify('Нечего показывать');
        if (!window.Lampa || !Lampa.Select || typeof Lampa.Select.show !== 'function') return notify(title);

        try {
            Lampa.Select.show({
                title: title,
                items: items,
                onSelect: function (item) {
                    window.TranslationSubRuntime.restoreController(previousController, 'content');
                    if (item && typeof item.onclick === 'function') {
                        setTimeout(function () {
                            if (token === dialogToken) item.onclick();
                        }, 0);
                    }
                },
                onBack: function () {
                    dialogToken++;
                    window.TranslationSubRuntime.restoreController(previousController, 'content');
                }
            });
        } catch (e) {
            window.TranslationSubRuntime.restoreController(previousController, 'content');
        }
    }

    function api() {
        return window.TranslationSubApi;
    }

    function refreshState(result) {
        try {
            var badge = window.TranslationSubBadgeState;
            if (badge && typeof badge.applyCommand === 'function' && badge.applyCommand(result)) {
                scheduleApply(lastEvent, 0);
                return;
            }
            if (badge && typeof badge.refresh === 'function') badge.refresh();
        } catch (e) {}
        scheduleApply(lastEvent, 0);
    }

    function commandFailed(result, fallback) {
        var error = result && result.error ? String(result.error) : '';
        if (error === 'sources_disabled') return 'Выберите балансеры в настройках TranslationSub';
        if (error === 'voice_not_found') return 'Озвучка больше недоступна';
        if (error === 'subscription_not_found') return 'Подписка уже удалена';
        return fallback;
    }

    function subscribe(raw, state, voice) {
        if (busy) return;
        var client = api();
        if (!client || typeof client.subscribe !== 'function') return notify('TranslationSub API недоступен');

        busy = true;
        client.subscribe(raw, voice.id, voice.name, function (result) {
            busy = false;
            if (!result || result.success !== true) {
                notify(commandFailed(result, 'Не удалось оформить подписку'));
                refreshState(result);
                return;
            }

            var season = state && state.content ? Number(state.content.season || 1) : 1;
            notify('Подписка оформлена · ' + String(voice.name || 'Озвучка') + ' · ' + season + ' сезон');
            refreshState(result);
        }, function () {
            busy = false;
            notify('Не удалось оформить подписку');
        });
    }

    function unsubscribe(state, voice) {
        if (busy) return;
        var client = api();
        var id = String(voice && voice.subscriptionId || '');
        if (!client || typeof client.unsubscribe !== 'function') return notify('TranslationSub API недоступен');
        if (!id) return notify('Не удалось определить подписку для удаления');

        busy = true;
        client.unsubscribe(id, function (result) {
            busy = false;
            if (!result || result.success !== true) {
                notify(commandFailed(result, 'Не удалось отписаться'));
                refreshState(result);
                return;
            }

            var season = state && state.content ? Number(state.content.season || 1) : 1;
            notify('Вы отписались · ' + String(voice.name || 'Озвучка') + ' · ' + season + ' сезон');
            refreshState(result);
        }, function () {
            busy = false;
            notify('Не удалось отписаться');
        });
    }

    function openForItem(object) {
        var raw = transportPayload(object || {});
        var client = api();
        if (!client || typeof client.contentState !== 'function')
            return notify('TranslationSub API недоступен');

        var previousController = window.TranslationSubRuntime.controllerName('content');
        var token = ++dialogToken;
        client.contentState(raw, function (state) {
            if (token !== dialogToken || !activeFull()) return;
            state = state && typeof state === 'object' ? state : {};

            if (!state.eligible) {
                if (state.reason === 'sources_disabled')
                    notify('Выберите балансеры для опроса в настройках TranslationSub');
                else if (state.reason === 'identity_unresolved')
                    notify('Не удалось определить карточку сериала');
                return;
            }

            var voices = Array.isArray(state.voices) ? state.voices : [];
            if (!voices.length) {
                notify(state.reason === 'sources_disabled'
                    ? 'Выберите балансеры для опроса в настройках TranslationSub'
                    : ('Озвучки для ' + Number(state.content && state.content.season || 1) + ' сезона пока не найдены'));
                return;
            }

            var currentController = window.TranslationSubRuntime.controllerName(previousController);
            if (currentController !== previousController) return;

            var items = voices.map(function (voice) {
                return {
                    title: (voice.subscribed ? '✓ ' : '') + String(voice.name || 'Озвучка'),
                    subtitle: String(voice.subtitle || ''),
                    selected: !!voice.subscribed,
                    onclick: function () {
                        if (voice.subscribed) unsubscribe(state, voice);
                        else subscribe(raw, state, voice);
                    }
                };
            });

            showSelect('Озвучки · ' + Number(state.content && state.content.season || 1) + ' сезон', items, token, previousController);
        }, function () {
            if (token === dialogToken) notify('Не удалось загрузить список озвучек');
        });
    }

    function buttonRoot(event) {
        var root = null;
        try {
            if (event && event.object && event.object.activity && typeof event.object.activity.render === 'function')
                root = event.object.activity.render();
        } catch (e) {}
        if (!root || !root.length) {
            root = $('.full-start').first();
            if (!root.length) root = $('.full-start-new').first();
        }
        return root;
    }

    function removeButton(event) {
        if (typeof $ !== 'function') return;
        var root = buttonRoot(event);
        if (root && root.length) root.find('.translationsub-full-button').remove();
    }

    function ensureButton(event) {
        if (typeof $ !== 'function') return null;
        var root = buttonRoot(event);
        if (!root || !root.length) return null;
        var button = root.find('.translationsub-full-button').first();
        if (button.length) return button;
        var row = root.find('.full-start-new__buttons').first();
        if (!row.length) row = root.find('.full-start__buttons').first();
        if (!row.length) return null;
        button = $('<div class="full-start__button selector translationsub-full-button"></div>');
        row.append(button);
        return button;
    }

    function renderButton(button, buttonState) {
        if (!button || !button.length) return;
        buttonState = buttonState && typeof buttonState === 'object' ? buttonState : {};
        var active = !!buttonState.subscribed;
        button.empty().append(bellSvg()).append('<span>Озвучки</span>');
        button.toggleClass('translationsub-full-button--subscribed', active);
        button.attr('title', String(buttonState.title || (active ? 'Озвучки · подписка активна' : 'Подписки на озвучки')));
    }

    function applyButton(event, token) {
        if (typeof $ !== 'function') return;
        var client = api();
        if (!client || typeof client.contentSummary !== 'function') return;

        var payload = event && (event.data || event.object) || {};
        var raw = transportPayload(payload);

        client.contentSummary(raw, function (state) {
            if (token !== viewToken || !activeFull()) return;
            state = state && typeof state === 'object' ? state : {};

            if (!state.eligible) {
                removeButton(event);
                return;
            }

            var button = ensureButton(event);
            if (!button || !button.length) return;
            button.off('.translationsubCardFlow');
            button.attr('data-translationsub-card-flow', '1');
            renderButton(button, state.button);
            try {
                if (window.TranslationSubUi && typeof window.TranslationSubUi.refresh === 'function')
                    window.TranslationSubUi.refresh();
            } catch (e) {}
            button.on('hover:enter.translationsubCardFlow', function () { openForItem(raw); });
            button.on('hover:long.translationsubCardFlow', function () {
                try {
                    if (window.TranslationSub && typeof window.TranslationSub.openSubscriptions === 'function')
                        window.TranslationSub.openSubscriptions();
                } catch (e) {}
            });
        }, function () {
            if (token === viewToken) removeButton(event);
        });
    }

    function scheduleApply(event, delay) {
        lastEvent = event || lastEvent;
        clearTimeout(applyTimer);
        var token = viewToken;
        applyTimer = setTimeout(function () {
            if (lastEvent && token === viewToken) applyButton(lastEvent, token);
        }, typeof delay === 'number' ? delay : 80);
    }

    function bind() {
        if (!Lampa.Listener || typeof Lampa.Listener.follow !== 'function') return;
        Lampa.Listener.follow('full', function (event) {
            if (!event || event.type !== 'complite') return;
            viewToken++;
            dialogToken++;
            scheduleApply(event, 80);
        });
    }

    function start() {
        bind();
        window.TranslationSubCardFlow = {
            refreshButton: function () { scheduleApply(lastEvent, 0); }
        };
    }

    window.TranslationSubRuntime.onReady(start);
})();