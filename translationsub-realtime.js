(function () {
    'use strict';

    if (window.__TranslationSubRealtimeStarted) return;
    window.__TranslationSubRealtimeStarted = true;

    var socket = null;
    var reconnectTimer = null;
    var reconnectDelay = 2000;
    var pingTimer = null;
    var refreshTimer = null;
    var registeredProfileId = null;

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

    function api() {
        return window.TranslationSubApi || null;
    }

    function currentProfileId() {
        var client = api();
        if (!client || typeof client.profileId !== 'function') return '0';
        try { return String(client.profileId() || '0'); }
        catch (e) { return '0'; }
    }

    function shouldConnect() {
        try { if (document.hidden) return false; } catch (e) {}
        return true;
    }

    function connectionId() {
        var value = String(storageGet('translationsub_nws_id', '') || '');
        if (value) return value;

        try {
            if (window.Lampa && Lampa.Utils && typeof Lampa.Utils.uid === 'function')
                value = String(Lampa.Utils.uid(32) || '').toLowerCase();
        } catch (e) {}
        if (!value) value = 'ts' + Math.random().toString(36).slice(2) + Date.now().toString(36);
        value = value.replace(/[^a-z0-9_-]+/gi, '').slice(0, 64);
        storageSet('translationsub_nws_id', value);
        return value;
    }

    function websocketUrl() {
        var client = api();
        var base = client && typeof client.host === 'function' ? String(client.host() || '') : '';
        if (!base) return '';
        if (/^https:/i.test(base)) base = base.replace(/^https:/i, 'wss:');
        else if (/^http:/i.test(base)) base = base.replace(/^http:/i, 'ws:');
        else return '';
        return base + '/nws?id=' + encodeURIComponent(connectionId()) + '&ver=1';
    }

    function send(method, args) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return false;
        try {
            socket.send(JSON.stringify({ method: method, args: args || [] }));
            return true;
        } catch (e) { return false; }
    }

    function register() {
        var client = api();
        if (!client || typeof client.uid !== 'function' || typeof client.profileId !== 'function') return false;
        var uid = String(client.uid() || '');
        if (!uid) return false;

        var profileId = currentProfileId();
        if (!send('TranslationSubRegister', [uid, profileId])) return false;
        registeredProfileId = profileId;
        return true;
    }

    function refreshSnapshot() {
        try {
            if (window.TranslationSubBadgeState && typeof window.TranslationSubBadgeState.refresh === 'function')
                window.TranslationSubBadgeState.refresh();
        } catch (e) {}
    }

    function scheduleRefresh() {
        clearTimeout(refreshTimer);
        refreshTimer = setTimeout(refreshSnapshot, 50);
    }

    function startPing() {
        stopPing();
        pingTimer = setInterval(function () {
            if (!socket || socket.readyState !== WebSocket.OPEN) return;
            try { socket.send('ping'); } catch (e) {}
        }, 50000);
    }

    function stopPing() {
        if (pingTimer) clearInterval(pingTimer);
        pingTimer = null;
    }

    function clearReconnect() {
        if (reconnectTimer) clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }

    function scheduleReconnect() {
        if (!shouldConnect() || reconnectTimer) return;
        reconnectTimer = setTimeout(function () {
            reconnectTimer = null;
            connect();
        }, reconnectDelay);
        reconnectDelay = Math.min(30000, Math.round(reconnectDelay * 1.7));
    }

    function disconnect() {
        clearReconnect();
        stopPing();
        registeredProfileId = null;

        var current = socket;
        socket = null;
        if (!current) return;

        try {
            if (current.readyState === WebSocket.OPEN || current.readyState === WebSocket.CONNECTING)
                current.close();
        } catch (e) {}
    }

    function syncProfile() {
        var profileId = currentProfileId();
        if (registeredProfileId === profileId) return;

        if (socket && socket.readyState === WebSocket.OPEN) {
            if (register()) scheduleRefresh();
            return;
        }

        if (shouldConnect()) connect();
    }

    function onMessage(event) {
        if (!event || event.data === 'pong') return;
        var message = null;
        try { message = JSON.parse(event.data); } catch (e) { return; }
        if (!message || typeof message.method !== 'string') return;

        if (message.method === 'Connected') {
            register();
            scheduleRefresh();
            return;
        }

        if (message.method === 'TranslationSubChanged') scheduleRefresh();
    }

    function connect() {
        if (!shouldConnect() || typeof WebSocket !== 'function') return;
        if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;

        var url = websocketUrl();
        if (!url) return;

        var current;
        try { current = new WebSocket(url); }
        catch (e) { scheduleReconnect(); return; }

        socket = current;
        current.onopen = function () {
            if (socket !== current) return;
            reconnectDelay = 2000;
            startPing();
        };
        current.onmessage = function (event) {
            if (socket === current) onMessage(event);
        };
        current.onerror = function () {};
        current.onclose = function () {
            if (socket !== current) return;
            stopPing();
            socket = null;
            registeredProfileId = null;
            scheduleReconnect();
        };
    }

    function bindProfileChanges() {
        try {
            if (Lampa.Storage && Lampa.Storage.listener && typeof Lampa.Storage.listener.follow === 'function') {
                Lampa.Storage.listener.follow('change', function (event) {
                    if (event && event.name === 'lampac_profile_id') syncProfile();
                });
            }
        } catch (e) {}

        try {
            if (Lampa.Account && Lampa.Account.listener && typeof Lampa.Account.listener.follow === 'function') {
                Lampa.Account.listener.follow('profile_select', syncProfile);
                Lampa.Account.listener.follow('profile_check', syncProfile);
            }
        } catch (e2) {}
    }

    function start() {
        bindProfileChanges();
        connect();

        try {
            document.addEventListener('visibilitychange', function () {
                if (document.hidden) {
                    disconnect();
                    return;
                }
                reconnectDelay = 2000;
                connect();
            });
        } catch (e) {}

        try {
            // navigator.onLine is only a browser hint and can be false while a
            // local Lampac server is still reachable. Do not tear down LAN NWS
            // on `offline`; `online` remains a useful prompt to retry quickly.
            window.addEventListener('online', function () {
                reconnectDelay = 2000;
                clearReconnect();
                connect();
            });
        } catch (e2) {}
    }

    window.TranslationSubRuntime.onReady(start);
})();
