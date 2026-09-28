(function () {
    'use strict';

    if (window.__TranslationSubBadgeStateStarted) return;
    window.__TranslationSubBadgeStateStarted = true;

    var state = {
        count: 0,
        updates: [],
        snapshot: null,
        requestSeq: 0,
        loading: false,
        waiters: [],
        drawerOpening: false,
        listeners: [],
        snapshotProfileId: null,
        snapshotGeneratedAt: 0
    };

    function textCount(count) {
        count = Number(count) || 0;
        return count > 99 ? '99+' : String(count);
    }

    function currentProfileId() {
        try {
            var api = window.TranslationSubApi;
            if (api && typeof api.profileId === 'function') return String(api.profileId() || '0');
        } catch (e) {}
        return '0';
    }

    function snapshotProfileId(snapshot) {
        if (!snapshot || typeof snapshot !== 'object') return '';
        var value = snapshot.profileId;
        if (value === undefined || value === null) value = snapshot.ProfileId;
        return value === undefined || value === null ? '' : String(value || '0');
    }

    function snapshotGeneratedAt(snapshot) {
        if (!snapshot || typeof snapshot !== 'object') return 0;
        var value = snapshot.generatedAt;
        if (value === undefined || value === null) value = snapshot.GeneratedAt;
        if (value === undefined || value === null || value === '') return 0;
        var stamp = Date.parse(String(value));
        return isNaN(stamp) ? 0 : stamp;
    }

    function canCommitSnapshot(snapshot) {
        if (!snapshot || typeof snapshot !== 'object') return false;

        var currentProfile = currentProfileId();
        var profile = snapshotProfileId(snapshot) || currentProfile;
        if (profile !== currentProfile) return false;

        var generatedAt = snapshotGeneratedAt(snapshot);
        if (state.snapshotProfileId === profile && generatedAt && state.snapshotGeneratedAt && generatedAt < state.snapshotGeneratedAt)
            return false;

        return true;
    }

    function render() {
        if (typeof $ !== 'function') return;
        var count = Number(state.count) || 0;

        $('.translationsub-head').each(function () {
            var head = $(this);
            head.toggleClass('translationsub-head--has-updates', count > 0);

            var badge = head.children('.translationsub-state-badge').first();
            if (!badge.length && count > 0) {
                badge = $('<div class="translationsub-state-badge"></div>');
                head.append(badge);
            }
            if (badge.length) {
                if (count > 0) badge.text(textCount(count)).show();
                else badge.hide();
            }
        });

        $('.translationsub-menu-item').each(function () {
            var item = $(this);
            var badge = item.children('.translationsub-state-menu-badge').first();
            if (!badge.length && count > 0) {
                badge = $('<div class="translationsub-state-menu-badge"></div>');
                item.append(badge);
            }
            if (badge.length) {
                if (count > 0) badge.text(textCount(count)).show();
                else badge.hide();
            }
        });
    }

    function view() {
        return {
            count: Number(state.count) || 0,
            updates: state.updates.slice(),
            snapshot: state.snapshot
        };
    }

    function emit() {
        var current = view();
        state.listeners.slice().forEach(function (listener) {
            try { listener(current); } catch (e) {}
        });
    }

    function subscribe(listener) {
        if (typeof listener !== 'function') return function () {};
        if (state.listeners.indexOf(listener) === -1) state.listeners.push(listener);

        try { listener(view()); } catch (e) {}

        return function () {
            var index = state.listeners.indexOf(listener);
            if (index !== -1) state.listeners.splice(index, 1);
        };
    }

    function commitSnapshot(snapshot) {
        if (!canCommitSnapshot(snapshot)) return null;

        var profile = snapshotProfileId(snapshot) || currentProfileId();
        var generatedAt = snapshotGeneratedAt(snapshot);
        if (state.snapshotProfileId !== profile) {
            state.snapshotProfileId = profile;
            state.snapshotGeneratedAt = 0;
        }
        if (generatedAt) state.snapshotGeneratedAt = Math.max(state.snapshotGeneratedAt, generatedAt);

        var updates = Array.isArray(snapshot.updates) ? snapshot.updates : [];
        var badge = snapshot.badge && typeof snapshot.badge === 'object' ? snapshot.badge : {};
        var count = Number(badge.count);

        state.snapshot = snapshot;
        state.updates = updates;
        state.count = isNaN(count) ? updates.length : Math.max(0, count);
        render();
        emit();
        return updates.slice();
    }

    function finishRefresh(updates) {
        state.loading = false;
        var waiters = state.waiters.splice(0);
        waiters.forEach(function (done) {
            try { done(updates.slice()); } catch (e) {}
        });
    }

    function applySnapshot(snapshot) {
        var updates = commitSnapshot(snapshot);
        if (!updates) return false;

        state.requestSeq++;
        if (state.loading) finishRefresh(updates);
        return true;
    }

    function applyCommand(result) {
        if (!result || result.success !== true || !result.snapshot || typeof result.snapshot !== 'object')
            return null;
        if (!applySnapshot(result.snapshot)) return null;
        return view();
    }

    function startRefresh() {
        if (state.loading) return;

        var api = window.TranslationSubApi;
        if (!api || typeof api.snapshot !== 'function') {
            render();
            finishRefresh(state.updates.slice());
            return;
        }

        state.loading = true;
        var requestedProfile = currentProfileId();
        var seq = ++state.requestSeq;
        api.snapshot(function (snapshot) {
            if (seq !== state.requestSeq) return;

            if (requestedProfile !== currentProfileId()) {
                state.loading = false;
                startRefresh();
                return;
            }

            var updates = commitSnapshot(snapshot);
            finishRefresh(updates || state.updates.slice());
        }, function () {
            if (seq !== state.requestSeq) return;

            if (requestedProfile !== currentProfileId()) {
                state.loading = false;
                startRefresh();
                return;
            }

            render();
            finishRefresh(state.updates.slice());
        });
    }

    function refresh(done) {
        done = typeof done === 'function' ? done : function () {};
        state.waiters.push(done);
        startRefresh();
    }

    function openDrawerSynced() {
        if (state.drawerOpening) return;
        state.drawerOpening = true;

        refresh(function (updates) {
            state.drawerOpening = false;

            try {
                if (window.TranslationSubNotice && typeof window.TranslationSubNotice.open === 'function') {
                    window.TranslationSubNotice.open(updates);
                    return;
                }
            } catch (e) {}

            try {
                if (window.TranslationSub && typeof window.TranslationSub.openSubscriptions === 'function')
                    window.TranslationSub.openSubscriptions();
            } catch (e2) {}
        });
    }

    function start() {
        refresh();

        try {
            document.addEventListener('visibilitychange', function () {
                if (!document.hidden) refresh();
            });
        } catch (e) {}

        window.TranslationSubBadgeState = {
            refresh: refresh,
            applyCommand: applyCommand,
            subscribe: subscribe,
            render: render,
            open: openDrawerSynced
        };
    }

    window.TranslationSubRuntime.onReady(start);
})();
