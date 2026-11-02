/**
 * TranslationSub — управление озвучками и подписками из карточки сериала.
 *
 * Здесь находятся логика кнопки «Озвучки», индикация загрузки,
 * выбор перевода, подписка / отписка и синхронизация с API Lampa.
 *
 * Клиент может предположить, что карточка является сериалом,
 * но окончательное решение о доступности подписок принимает сервер.
 */
(function () {
    'use strict';

    if (window.__TranslationSubCardFlowStarted) return;
    window.__TranslationSubCardFlowStarted = true;

    var lastEvent = null;
    var applyTimer = null;
    var viewToken = 0;
    var dialogToken = 0;
    var busy = false;
    var voiceRequest = null;

    // -------------------------------------------------------------------------
    // Индикация загрузки списка озвучек
    // -------------------------------------------------------------------------

    function setVoiceLoading(button, loading) {
        if (!button || !button.length) return;

        button.toggleClass('translationsub-full-button--loading', loading);
        button.attr('aria-busy', loading ? 'true' : 'false');

        if (loading) {
            button.find('.translationsub-full-button__spinner').remove();
            button.prepend(
                '<i class="translationsub-full-button__spinner" aria-hidden="true"></i>'
            );
            button.find('span').text('Собираем озвучки…');
        } else {
            button.find('.translationsub-full-button__spinner').remove();
            button.find('span').text('Озвучки');
        }
    }

    function finishVoiceRequest(request) {
        // Не сбрасываем состояние, если ответ принадлежит старому запросу.
        if (voiceRequest !== request) return;

        setVoiceLoading(request.button, false);
        voiceRequest = null;
    }

    function injectLoadingStyle() {
        if (document.getElementById('translationsub-card-loading-style')) return;

        var style = document.createElement('style');
        style.id = 'translationsub-card-loading-style';

        // CSS хранится построчно для удобного редактирования.
        style.textContent = [
            '.translationsub-full-button__spinner {',
            '    display: block;',
            '    width: 1.5em;',
            '    height: 1.5em;',
            '    flex: 0 0 1.5em;',
            '    box-sizing: border-box;',
            '    border: .15em solid currentColor;',
            '    border-right-color: transparent;',
            '    border-radius: 50%;',
            '    animation: translationsub-card-spin .8s linear infinite;',
            '}',
            '.translationsub-full-button--loading > .translationsub-full-button__bell {',
            '    display: none;',
            '}',
            '.translationsub-full-button--loading.translationsub-full-button--mobile > span {',
            '    display: block !important;',
            '}',
            '@keyframes translationsub-card-spin {',
            '    to { transform: rotate(360deg); }',
            '}',
            '@media (prefers-reduced-motion: reduce) {',
            '    .translationsub-full-button__spinner {',
            '        animation: none;',
            '    }',
            '}'
        ].join('\n');

        (document.head || document.documentElement).appendChild(style);
    }

    // -------------------------------------------------------------------------
    // Уведомления и подготовка данных карточки
    // -------------------------------------------------------------------------

    function notify(text) {
        try {
            if (window.Lampa && Lampa.Noty && typeof Lampa.Noty.show === 'function') {
                Lampa.Noty.show(text);
                return;
            }
        } catch (e) {
            // Lampa может быть недоступна: используем консоль.
        }

        try {
            console.log('[TranslationSub]', text);
        } catch (e2) {
            // Отсутствие консоли не должно мешать работе интерфейса.
        }
    }

    function bellSvg() {
        return '<svg class="translationsub-full-button__bell" viewBox="0 0 24 24" aria-hidden="true"></svg>';
    }

    function primitive(value) {
        return value === null ||
            value === undefined ||
            typeof value === 'string' ||
            typeof value === 'number' ||
            typeof value === 'boolean';
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

        try {
            return JSON.parse(JSON.stringify(value));
        } catch (e) {
            return null;
        }
    }

    // Копируем только нужные API поля, исключая внутренние объекты Lampa.
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
        try {
            return typeof $ === 'function' &&
                $('.full-start:visible,.full-start-new:visible').length > 0;
        } catch (e) {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Диалог выбора озвучек
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Оформление подписки и отмена подписки
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Загрузка озвучек и обработка ответа API
    // -------------------------------------------------------------------------

    function openForItem(object, button) {
        // Пока идёт загрузка, повторный запрос не запускается.
        if (voiceRequest) return;

        var raw = transportPayload(object || {});
        var client = api();
        if (!client || typeof client.contentState !== 'function')
            return notify('TranslationSub API недоступен');

        var previousController = window.TranslationSubRuntime.controllerName('content');
        var token = ++dialogToken;
        var request = { button: button, view: viewToken };
        voiceRequest = request;
        setVoiceLoading(button, true);
        client.contentState(raw, function (state) {
            finishVoiceRequest(request);
            if (request.view !== viewToken || token !== dialogToken || !activeFull()) return;
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
            finishVoiceRequest(request);
            if (request.view === viewToken && token === dialogToken && activeFull())
                notify('Не удалось загрузить список озвучек');
        });
    }

    // -------------------------------------------------------------------------
    // Поиск и создание кнопки в карточке
    // -------------------------------------------------------------------------

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
        var title = buttonState.title ||
            (active ? 'Озвучки · подписка активна' : 'Подписки на озвучки');

        button.empty()
            .append(bellSvg())
            .append('<span>Озвучки</span>');

        button.toggleClass('translationsub-full-button--subscribed', active);
        button.attr('title', String(title));
    }

    // Это лишь подсказка для ранней отрисовки кнопки.
    // Сервер позже подтвердит, можно ли оформлять подписку.
    function serialCardHint(raw) {
        var card = raw.card || {};
        var method = String(raw.method || card.method || '').toLowerCase();
        var type = String(
            raw.media_type || raw.type || card.media_type || card.type || ''
        ).toLowerCase();

        function yes(value) {
            return value === true ||
                value === 1 ||
                value === '1' ||
                String(value).toLowerCase() === 'true';
        }

        return method === 'tv' ||
            method === 'serial' ||
            type === 'tv' ||
            type === 'serial' ||
            type === 'series' ||
            yes(raw.serial) ||
            yes(card.serial) ||
            yes(raw.is_serial) ||
            yes(card.is_serial) ||
            yes(card.isSerial) ||
            Number(raw.season || card.season || 0) > 0 ||
            Number(card.number_of_seasons || card.seasons_count || 0) > 0 ||
            Number(card.number_of_episodes || card.episodes_count || 0) > 0 ||
            !!(card.first_air_date ||
                card.last_air_date ||
                card.last_episode_to_air ||
                card.next_episode_to_air) ||
            !!(card.episode_run_time && card.episode_run_time.length) ||
            !!(card.seasons && card.seasons.some(function (season) {
                return Number(season.season_number || season.number || 0) > 0;
            })) ||
            !!(card.original_name && !card.original_title);
    }

    function updateCardButton(event, raw, buttonState) {
        var button = ensureButton(event);
        if (!button || !button.length) return;
        button.off('.translationsubCardFlow');
        button.attr('data-translationsub-card-flow', '1');
        renderButton(button, buttonState || {
            subscribed: button.hasClass('translationsub-full-button--subscribed')
        });
        if (voiceRequest && voiceRequest.view === viewToken) {
            voiceRequest.button = button;
            setVoiceLoading(button, true);
        }
        try {
            if (window.TranslationSubUi && typeof window.TranslationSubUi.refresh === 'function')
                window.TranslationSubUi.refresh();
        } catch (e) {}
        button.on('hover:enter.translationsubCardFlow', function () {
            openForItem(raw, button);
        });
        button.on('hover:long.translationsubCardFlow', function () {
            try {
                if (window.TranslationSub && typeof window.TranslationSub.openSubscriptions === 'function')
                    window.TranslationSub.openSubscriptions();
            } catch (e) {}
        });
    }

    // -------------------------------------------------------------------------
    // Синхронизация кнопки с API и экраном
    // -------------------------------------------------------------------------

    function applyButton(event, token) {
        if (typeof $ !== 'function' || token !== viewToken) return;
        var payload = event && (event.data || event.object) || {};
        var raw = transportPayload(payload);

        // Не ждём сети: подходящая карточка сразу получает кнопку.
        // Если сервер вернёт eligible=false, кнопку уберём.
        if (serialCardHint(raw)) updateCardButton(event, raw);

        var client = api();
        if (!client || typeof client.contentSummary !== 'function') return;
        client.contentSummary(raw, function (state) {
            if (token !== viewToken || !activeFull()) return;
            state = state && typeof state === 'object' ? state : {};
            if (!state.eligible) {
                removeButton(event);
                return;
            }
            updateCardButton(event, raw, state.button);
        }, function () {
            // Сетевой сбой не скрывает кнопку, которая уже доступна.
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

    // -------------------------------------------------------------------------
    // Подписка на события Lampa и запуск
    // -------------------------------------------------------------------------

    function bind() {
        if (!Lampa.Listener || typeof Lampa.Listener.follow !== 'function') return;
        Lampa.Listener.follow('full', function (event) {
            if (!event || event.type !== 'complite') return;
            if (voiceRequest) finishVoiceRequest(voiceRequest);
            viewToken++;
            dialogToken++;
            lastEvent = event;
            clearTimeout(applyTimer);
            applyButton(event, viewToken);
        });
    }

    function start() {
        injectLoadingStyle();
        bind();
        window.TranslationSubCardFlow = {
            refreshButton: function () {
                scheduleApply(lastEvent, 0);
            }
        };
    }

    window.TranslationSubRuntime.onReady(start);
})();