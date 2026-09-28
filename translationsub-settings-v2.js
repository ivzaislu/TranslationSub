(function () {
    'use strict';

    if (window.__TranslationSubSettingsV2Started) return;
    window.__TranslationSubSettingsV2Started = true;

    var ROOT = 'translationsub_settings';
    var BALANCERS = 'translationsub_balancers';
    var KEYS = {
        interval: 'translationsub_interval_hours',
        cardSource: 'translationsub_card_source',
        smartTmdb: 'translationsub_tmdb_schedule',
        tmdbRefreshHours: 'translationsub_tmdb_refresh_hours',
        endedRefreshDays: 'translationsub_tmdb_ended_days',
        newSeasonMode: 'translationsub_new_season_mode'
    };

    var serverReady = false;
    var serverSchema = {};
    var availableItems = [];
    var selectedSources = [];
    var sourceParamKeys = {};
    var settingsLoadSeq = 0;
    var reloadTimer = null;
    var syncInFlight = false;
    var pendingSettingsBody = null;

    function storageGet(name, fallback) {
        try {
            if (window.Lampa && Lampa.Storage && typeof Lampa.Storage.get === 'function')
                return Lampa.Storage.get(name, fallback);
        } catch (e) {}
        try {
            var value = localStorage.getItem(name);
            return value === null ? fallback : value;
        } catch (e2) { return fallback; }
    }

    function storageSet(name, value) {
        try {
            if (window.Lampa && Lampa.Storage && typeof Lampa.Storage.set === 'function') {
                Lampa.Storage.set(name, value);
                return;
            }
        } catch (e) {}
        try { localStorage.setItem(name, typeof value === 'string' ? value : JSON.stringify(value)); } catch (e2) {}
    }

    function boolValue(value, fallback) {
        if (value === true || value === 1 || value === '1' || value === 'true') return true;
        if (value === false || value === 0 || value === '0' || value === 'false') return false;
        return !!fallback;
    }

    function triggerStorageValue(value, fallback) {
        return boolValue(value, fallback) ? 'true' : 'false';
    }

    function settingBool(name, fallback) {
        var value = storageGet(name, fallback);
        if (value === true || value === 1 || value === '1' || value === 'true') return true;
        if (value === false || value === 0 || value === '0' || value === 'false') return false;
        return !!fallback;
    }

    function boolOrNull(name) {
        var value = storageGet(name, null);
        if (value === null || value === undefined || value === '') return null;
        if (value === true || value === 1 || value === '1' || value === 'true') return true;
        if (value === false || value === 0 || value === '0' || value === 'false') return false;
        return null;
    }

    function intOrNull(name) {
        var raw = storageGet(name, null);
        if (raw === null || raw === undefined || raw === '') return null;
        var value = parseInt(raw, 10);
        return isNaN(value) ? null : value;
    }

    function sourceList(value) {
        if (!Array.isArray(value)) return [];
        var seen = {};
        return value.map(function (item) { return String(item || '').trim(); })
            .filter(function (item) {
                if (!item || seen[item]) return false;
                seen[item] = true;
                return true;
            });
    }

    function notify(text) {
        try {
            if (window.Lampa && Lampa.Noty && typeof Lampa.Noty.show === 'function') Lampa.Noty.show(text);
        } catch (e) {}
    }

    function controllerName() {
        try {
            if (window.TranslationSubRuntime && typeof window.TranslationSubRuntime.controllerName === 'function')
                return window.TranslationSubRuntime.controllerName('');
        } catch (e) {}
        return '';
    }

    function settingsLayerOpen() {
        var name = controllerName();
        return name === 'settings' || name === 'select';
    }

    function rebuildSettingsSafe() {
        if (settingsLayerOpen()) return;
        rebuildSettings();
    }

    function pick(object, pascal, camel, fallback) {
        if (!object) return fallback;
        if (object[pascal] !== undefined && object[pascal] !== null) return object[pascal];
        if (object[camel] !== undefined && object[camel] !== null) return object[camel];
        return fallback;
    }

    function normalizeItems(value) {
        if (!Array.isArray(value)) return [];
        var seen = {};
        return value.map(function (item) {
            item = item || {};
            var id = String(item.id || '').trim();
            var name = String(item.name || id).trim();
            return { id: id, name: name || id };
        }).filter(function (item) {
            if (!item.id || seen[item.id]) return false;
            seen[item.id] = true;
            return true;
        });
    }

    function sourceParamKey(id) {
        var hash = 0;
        for (var i = 0; i < id.length; i++) hash = ((hash << 5) - hash + id.charCodeAt(i)) | 0;
        return 'translationsub_source_' + id.replace(/[^a-z0-9]+/gi, '_') + '_' + Math.abs(hash);
    }

    function settingsBellSvg() {
        return '<svg class="translationsub-settings-bell" width="37" height="37" viewBox="0 0 37 37" aria-hidden="true"></svg>';
    }

    function availableIdSet() {
        var result = {};
        availableItems.forEach(function (item) { result[item.id] = true; });
        return result;
    }

    function currentSelectionFromTriggers() {
        var available = availableIdSet();
        var result = selectedSources.filter(function (id) { return !available[id]; });

        availableItems.forEach(function (item) {
            var key = sourceParamKeys[item.id];
            if (key && settingBool(key, false)) result.push(item.id);
        });

        return sourceList(result);
    }

    function schemaField(name) {
        var field = serverSchema && serverSchema[name];
        return field && typeof field === 'object' ? field : null;
    }

    function schemaValues(name) {
        var field = schemaField(name);
        var values = field && field.values;
        return values && typeof values === 'object' && !Array.isArray(values) ? values : null;
    }

    function schemaDefault(name) {
        var field = schemaField(name);
        return field && field.defaultValue !== undefined ? field.defaultValue : null;
    }

    function applyCanonicalSettings(data) {
        data = data || {};
        var settings = data.settings && typeof data.settings === 'object' ? data.settings : {};
        serverSchema = data.schema && typeof data.schema === 'object' ? data.schema : {};
        serverReady = !!Object.keys(serverSchema).length;
        selectedSources = sourceList(pick(settings, 'Sources', 'sources', []));
        availableItems = normalizeItems(data.availableSourceItems || []);

        var interval = pick(settings, 'CheckIntervalHours', 'checkIntervalHours', null);
        var smart = pick(settings, 'UseTmdbSchedule', 'useTmdbSchedule', null);
        var tmdbHours = pick(settings, 'TmdbRefreshHours', 'tmdbRefreshHours', null);
        var endedDays = pick(settings, 'EndedRefreshDays', 'endedRefreshDays', null);
        var seasonMode = pick(settings, 'NewSeasonMode', 'newSeasonMode', null);
        if (interval !== null) storageSet(KEYS.interval, String(interval));
        if (smart !== null) storageSet(KEYS.smartTmdb, triggerStorageValue(smart, false));
        if (tmdbHours !== null) storageSet(KEYS.tmdbRefreshHours, String(tmdbHours));
        if (endedDays !== null) storageSet(KEYS.endedRefreshDays, String(endedDays));
        if (seasonMode !== null) storageSet(KEYS.newSeasonMode, String(seasonMode));

        availableItems.forEach(function (item) {
            var key = sourceParamKeys[item.id] || sourceParamKey(item.id);
            sourceParamKeys[item.id] = key;
            storageSet(key, triggerStorageValue(selectedSources.indexOf(item.id) >= 0, false));
        });
    }

    function refreshPlugin() {
        try {
            if (window.TranslationSubBadgeState && typeof window.TranslationSubBadgeState.refresh === 'function')
                window.TranslationSubBadgeState.refresh();
        } catch (e) {}

        try {
            var flow = window.TranslationSubCardFlow;
            if (flow && typeof flow.refreshButton === 'function') flow.refreshButton();
        } catch (e2) {}
    }

    function buildSettingsBody() {
        selectedSources = availableItems.length ? currentSelectionFromTriggers() : selectedSources.slice();
        var body = { sources: selectedSources.slice() };
        var interval = intOrNull(KEYS.interval);
        var smart = boolOrNull(KEYS.smartTmdb);
        var tmdbHours = intOrNull(KEYS.tmdbRefreshHours);
        var endedDays = intOrNull(KEYS.endedRefreshDays);
        var seasonMode = storageGet(KEYS.newSeasonMode, null);

        if (interval !== null) body.checkIntervalHours = interval;
        if (smart !== null) body.useTmdbSchedule = smart;
        if (tmdbHours !== null) body.tmdbRefreshHours = tmdbHours;
        if (endedDays !== null) body.endedRefreshDays = endedDays;
        if (seasonMode !== null && seasonMode !== '') body.newSeasonMode = String(seasonMode);
        return body;
    }

    function flushServerSettings() {
        if (syncInFlight || !pendingSettingsBody || !serverReady) return;

        var api = window.TranslationSubApi;
        if (!api || typeof api.updateSettings !== 'function') return;

        var body = pendingSettingsBody;
        pendingSettingsBody = null;
        syncInFlight = true;

        api.updateSettings(body, function (response) {
            syncInFlight = false;

            if (!response || response.success !== true) {
                notify('Не удалось сохранить настройки TranslationSub');
                if (pendingSettingsBody) flushServerSettings();
                else scheduleServerReload(1000);
                return;
            }

            if (pendingSettingsBody) {
                flushServerSettings();
                return;
            }

            applyCanonicalSettings(response);
            rebuildSettingsSafe();
            refreshPlugin();
        }, function () {
            syncInFlight = false;
            notify('Не удалось сохранить настройки TranslationSub');
            if (pendingSettingsBody) flushServerSettings();
            else scheduleServerReload(1000);
        });
    }

    function syncServerSettings() {
        if (!serverReady) return;
        clearTimeout(reloadTimer);
        reloadTimer = null;
        settingsLoadSeq++;
        pendingSettingsBody = buildSettingsBody();
        flushServerSettings();
    }

    function openBalancersSettings() {
        if (!window.Lampa || !Lampa.Settings || typeof Lampa.Settings.create !== 'function') {
            notify('Эта версия Lampa не поддерживает вложенные настройки');
            return;
        }
        try { if (Lampa.Controller && typeof Lampa.Controller.back === 'function') Lampa.Controller.back(); } catch (e) {}
        setTimeout(function () {
            try {
                Lampa.Settings.create(BALANCERS, {
                    onBack: function () { try { Lampa.Settings.create(ROOT); } catch (e) {} }
                });
            } catch (e2) { notify('Не удалось открыть балансеры'); }
        }, 0);
    }

    function addBalancer(item) {
        var key = sourceParamKey(item.id);
        sourceParamKeys[item.id] = key;
        storageSet(key, triggerStorageValue(selectedSources.indexOf(item.id) >= 0, false));

        Lampa.SettingsApi.addParam({
            component: BALANCERS,
            param: { name: key, type: 'trigger', values: '', 'default': false },
            field: {
                name: item.name,
                description: 'Lampac: ' + item.id + '. Использовать этот балансер для поиска озвучек и новых серий.'
            },
            onChange: function (value) {
                storageSet(key, triggerStorageValue(value, settingBool(key, false)));
                selectedSources = currentSelectionFromTriggers();
                syncServerSettings();
            }
        });
    }

    function addServerSelect(schemaName, storageKey, name, description) {
        var values = schemaValues(schemaName);
        var defaultValue = schemaDefault(schemaName);
        if (!values || defaultValue === null) return;

        Lampa.SettingsApi.addParam({
            component: ROOT,
            param: { name: storageKey, type: 'select', values: values, 'default': String(defaultValue) },
            field: { name: name, description: description },
            onChange: function (value) {
                if (value !== undefined && value !== null) storageSet(storageKey, String(value));
                syncServerSettings();
            }
        });
    }

    function prepareBalancersComponent() {
        try {
            if (typeof Lampa.SettingsApi.removeComponent === 'function')
                Lampa.SettingsApi.removeComponent(BALANCERS);
        } catch (e) {
            try {
                if (typeof Lampa.SettingsApi.removeParams === 'function')
                    Lampa.SettingsApi.removeParams(BALANCERS);
            } catch (e2) {}
        }

        try {
            if (Lampa.Template && typeof Lampa.Template.add === 'function')
                Lampa.Template.add('settings_' + BALANCERS, '<div></div>');
        } catch (e3) {}

        try {
            if (Lampa.Settings && typeof Lampa.Settings.main === 'function') {
                var main = Lampa.Settings.main();
                if (main && typeof main.render === 'function')
                    main.render().find('[data-component="' + BALANCERS + '"]').remove();
            }
        } catch (e4) {}
    }

    function rebuildSettings() {
        if (!window.Lampa || !Lampa.SettingsApi) return;

        try { if (typeof Lampa.SettingsApi.removeParams === 'function') Lampa.SettingsApi.removeParams(ROOT); } catch (e) {}
        prepareBalancersComponent();

        Lampa.SettingsApi.addComponent({ component: ROOT, name: 'Подписки на озвучки', icon: settingsBellSvg() });

        if (serverReady) {
            var available = availableIdSet();
            var activeCount = selectedSources.filter(function (id) { return available[id]; }).length;
            Lampa.SettingsApi.addParam({
                component: ROOT,
                param: { name: 'translationsub_open_balancers', type: 'button', 'default': '' },
                field: {
                    name: 'Балансеры для опроса',
                    description: availableItems.length
                        ? 'Активно выбрано: ' + activeCount + ' из ' + availableItems.length
                        : 'Lampac не вернул доступных online-балансеров'
                },
                onChange: openBalancersSettings
            });

            addServerSelect(
                'checkIntervalHours',
                KEYS.interval,
                'Интервал активного опроса',
                'Как часто проверять выбранные балансеры после выхода серии по TMDB.'
            );

            var smartDefault = schemaDefault('useTmdbSchedule');
            if (smartDefault !== null) {
                Lampa.SettingsApi.addParam({
                    component: ROOT,
                    param: { name: KEYS.smartTmdb, type: 'trigger', values: '', 'default': !!smartDefault },
                    field: { name: 'Умное расписание TMDB', description: 'Не опрашивать балансеры до фактического выхода серии или сезона.' },
                    onChange: function (value) {
                        storageSet(KEYS.smartTmdb, triggerStorageValue(value, settingBool(KEYS.smartTmdb, !!smartDefault)));
                        syncServerSettings();
                    }
                });
            }

            addServerSelect(
                'tmdbRefreshHours',
                KEYS.tmdbRefreshHours,
                'Обновление расписания TMDB',
                'Как часто перепроверять расписание активных сериалов.'
            );
            addServerSelect(
                'endedRefreshDays',
                KEYS.endedRefreshDays,
                'Перепроверка завершённых сериалов',
                'Как часто TMDB проверяется на неожиданное продолжение.'
            );
            addServerSelect(
                'newSeasonMode',
                KEYS.newSeasonMode,
                'Когда начинается новый сезон',
                'Поведение подписки после появления следующего сезона.'
            );
        } else {
            Lampa.SettingsApi.addParam({
                component: ROOT,
                param: { name: 'translationsub_server_settings_unavailable', type: 'title', 'default': '' },
                field: { name: 'Серверные настройки временно недоступны' }
            });
        }

        Lampa.SettingsApi.addParam({
            component: ROOT,
            param: { name: KEYS.cardSource, type: 'select', values: { tmdb: 'TMDB', cub: 'CUB' }, 'default': 'tmdb' },
            field: { name: 'Источник карточки', description: 'Источник карточки Lampa при открытии сериала из подписок.' }
        });

        if (serverReady) {
            Lampa.SettingsApi.addParam({
                component: BALANCERS,
                param: { name: 'translationsub_balancers_title', type: 'title', 'default': '' },
                field: { name: 'Доступные в Lampac балансеры' }
            });
            availableItems.forEach(addBalancer);
        }
    }

    function scheduleServerReload(delay) {
        clearTimeout(reloadTimer);
        reloadTimer = setTimeout(function () {
            reloadTimer = null;
            loadServerSettings();
        }, typeof delay === 'number' ? delay : 15000);
    }

    function loadServerSettings() {
        var api = window.TranslationSubApi;
        if (!api || typeof api.settings !== 'function') return false;

        var seq = ++settingsLoadSeq;
        api.settings(function (data) {
            if (seq !== settingsLoadSeq) return;
            clearTimeout(reloadTimer);
            reloadTimer = null;
            applyCanonicalSettings(data);
            rebuildSettingsSafe();
        }, function () {
            if (seq !== settingsLoadSeq) return;
            serverReady = false;
            serverSchema = {};
            availableItems = [];
            selectedSources = [];
            rebuildSettingsSafe();
            scheduleServerReload(15000);
        });
        return true;
    }

    function start() {
        if (!Lampa.SettingsApi || !window.TranslationSubApi) return;
        loadServerSettings();
    }

    window.TranslationSubRuntime.onReady(start);
})();
