(function () {
    'use strict';

    if (window.__TranslationSubApiStarted) return;
    window.__TranslationSubApiStarted = true;

    var REQUEST_TIMEOUT = 30000;
    var HOST_STORAGE_KEY = 'translationsub_api_host';
    var resolvedHost = '';

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
        try { localStorage.setItem(name, value); } catch (e2) {}
    }

    function uid() {
        var value = String(storageGet('lampac_unic_id', '') || '');
        if (value) return value;

        try {
            if (window.Lampa && Lampa.Utils && typeof Lampa.Utils.uid === 'function')
                value = String(Lampa.Utils.uid(8) || '').toLowerCase();
        } catch (e) {}
        if (!value) value = Math.random().toString(36).slice(2, 10).toLowerCase();

        storageSet('lampac_unic_id', value);
        return value;
    }

    function profileId() {
        return String(storageGet('lampac_profile_id', '') || '0') || '0';
    }

    function isPluginUrl(value) {
        return /\/translationsub(?:\/plugin)?\.js(?:[?#]|$)/i.test(String(value || ''));
    }

    function originFromUrl(value) {
        value = String(value || '');
        if (!value) return '';

        try {
            if (typeof URL === 'function') return new URL(value, window.location.href).origin || '';
        } catch (e) {}

        try {
            var anchor = document.createElement('a');
            anchor.href = value;
            if (anchor.protocol && anchor.host) return anchor.protocol + '//' + anchor.host;
        } catch (e2) {}
        return '';
    }

    function rememberHost(value) {
        value = String(value || '').replace(/\/$/, '');
        if (!value) return '';
        resolvedHost = value;
        storageSet(HOST_STORAGE_KEY, value);
        return value;
    }

    function configuredPluginHost() {
        var plugins = storageGet('plugins', []);
        if (typeof plugins === 'string') {
            try { plugins = JSON.parse(plugins); } catch (e) { plugins = []; }
        }
        if (!Array.isArray(plugins)) return '';

        for (var i = plugins.length - 1; i >= 0; i--) {
            var item = plugins[i];
            var url = typeof item === 'string' ? item : (item && item.url);
            if (!isPluginUrl(url)) continue;
            var origin = originFromUrl(url);
            if (origin) return origin;
        }
        return '';
    }

    function host() {
        try {
            if (window.LampacHost) return String(window.LampacHost).replace(/\/$/, '');
            if (resolvedHost) return resolvedHost;

            var current = document.currentScript;
            if (current && isPluginUrl(current.src)) {
                var currentOrigin = originFromUrl(current.src);
                if (currentOrigin) return rememberHost(currentOrigin);
            }

            var scripts = document.getElementsByTagName('script');
            for (var i = scripts.length - 1; i >= 0; i--) {
                var src = scripts[i].src || '';
                if (!isPluginUrl(src)) continue;
                var scriptOrigin = originFromUrl(src);
                if (scriptOrigin) return rememberHost(scriptOrigin);
            }

            var configured = configuredPluginHost();
            if (configured) return rememberHost(configured);

            var cached = String(storageGet(HOST_STORAGE_KEY, '') || '').replace(/\/$/, '');
            if (cached) {
                resolvedHost = cached;
                return cached;
            }

            if (window.location.origin) return window.location.origin;
            if (window.location.protocol && window.location.host)
                return window.location.protocol + '//' + window.location.host;
            return '';
        } catch (e) { return ''; }
    }

    function query(params) {
        var parts = [];
        params = params || {};
        Object.keys(params).forEach(function (key) {
            var value = params[key];
            if (value === null || value === undefined || value === '') return;
            parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(String(value)));
        });
        return parts.join('&');
    }

    function parseJson(value) {
        if (value && typeof value === 'object' && !Array.isArray(value)) return value;
        if (typeof value !== 'string' || !value.trim()) throw new Error('Invalid JSON response');

        var parsed = JSON.parse(value);
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
            throw new Error('Invalid JSON response');
        return parsed;
    }

    function request(method, path, params, body, success, error) {
        success = typeof success === 'function' ? success : function () {};
        error = typeof error === 'function' ? error : function () {};

        params = params || {};
        if (!params.uid) params.uid = uid();

        var qs = query(params);
        var url = host() + path + (qs ? (path.indexOf('?') === -1 ? '?' : '&') + qs : '');
        var options = {
            method: method,
            cache: 'no-store',
            headers: { 'Content-Type': 'application/json; charset=utf-8' }
        };
        if (body !== null && body !== undefined && method !== 'GET')
            options.body = JSON.stringify(body);

        if (typeof fetch === 'function') {
            var settled = false;
            var controller = typeof AbortController === 'function' ? new AbortController() : null;
            if (controller) options.signal = controller.signal;

            var timeout = setTimeout(function () {
                if (settled) return;
                if (controller) {
                    try { controller.abort(); } catch (e) {}
                }
                settled = true;
                error(new Error('Request timeout'));
            }, REQUEST_TIMEOUT);

            fetch(url, options)
                .then(function (response) {
                    if (!response.ok) throw new Error('HTTP ' + response.status);
                    return response.text();
                })
                .then(function (text) {
                    if (settled) return;
                    var data = parseJson(text);
                    if (settled) return;
                    settled = true;
                    clearTimeout(timeout);
                    success(data);
                })
                .catch(function (reason) {
                    if (settled) return;
                    settled = true;
                    clearTimeout(timeout);
                    error(reason);
                });
            return;
        }

        try {
            var xhr = new XMLHttpRequest();
            var xhrSettled = false;

            function xhrSuccess(data) {
                if (xhrSettled) return;
                xhrSettled = true;
                success(data);
            }

            function xhrError(reason) {
                if (xhrSettled) return;
                xhrSettled = true;
                error(reason);
            }

            xhr.open(method, url, true);
            xhr.timeout = REQUEST_TIMEOUT;
            xhr.setRequestHeader('Content-Type', 'application/json; charset=utf-8');
            xhr.setRequestHeader('Cache-Control', 'no-cache');
            xhr.onreadystatechange = function () {
                if (xhr.readyState !== 4 || xhrSettled) return;
                if (xhr.status >= 200 && xhr.status < 300) {
                    try { xhrSuccess(parseJson(xhr.responseText)); }
                    catch (parseError) { xhrError(parseError); }
                } else {
                    xhrError(new Error('HTTP ' + xhr.status));
                }
            };
            xhr.onerror = function () { xhrError(new Error('Network error')); };
            xhr.ontimeout = function () { xhrError(new Error('Request timeout')); };
            xhr.send(options.body || null);
        } catch (e2) { error(e2); }
    }

    function snapshot(success, error) {
        request('GET', '/translationsub/v2/snapshot', {
            profile_id: profileId(),
            _ts: Date.now ? Date.now() : new Date().getTime()
        }, null, success, error);
    }

    function contentState(card, success, error) {
        request('POST', '/translationsub/v2/content-state', {}, card || {}, success, error);
    }

    function contentSummary(card, success, error) {
        request('POST', '/translationsub/v2/content-state', {}, {
            includeVoices: false,
            payload: card || {}
        }, success, error);
    }

    function subscribe(card, voiceId, voiceName, success, error) {
        request('POST', '/translationsub/v2/subscriptions', { profile_id: profileId() }, {
            card: card || {},
            voiceId: String(voiceId || ''),
            voiceName: String(voiceName || '')
        }, success, error);
    }

    function unsubscribe(subscriptionId, success, error) {
        var id = encodeURIComponent(String(subscriptionId || ''));
        request('POST', '/translationsub/v2/subscriptions/' + id + '/remove', { profile_id: profileId() }, {}, success, error);
    }

    function check(success, error) {
        request('POST', '/translationsub/v2/check', { profile_id: profileId() }, {}, success, error);
    }

    function settings(success, error) {
        request('GET', '/translationsub/v2/settings', {}, null, success, error);
    }

    function updateSettings(values, success, error) {
        request('POST', '/translationsub/v2/settings', {}, values || {}, success, error);
    }

    window.TranslationSubApi = {
        snapshot: snapshot,
        contentState: contentState,
        contentSummary: contentSummary,
        subscribe: subscribe,
        unsubscribe: unsubscribe,
        check: check,
        settings: settings,
        updateSettings: updateSettings,
        uid: uid,
        profileId: profileId,
        host: host
    };
})();
