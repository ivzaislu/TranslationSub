(function () {
    'use strict';

    if (window.__TranslationSubPluginStarted) return;
    window.__TranslationSubPluginStarted = true;

    var META = {
        name: 'Подписки на озвучки',
        version: '4.0.0-backend-first'
    };

    function log() {
        try {
            var args = Array.prototype.slice.call(arguments);
            args.unshift('[TranslationSub]');
            console.log.apply(console, args);
        } catch (e) {}
    }

    function createRuntime() {
        if (window.TranslationSubRuntime && typeof window.TranslationSubRuntime.onReady === 'function')
            return window.TranslationSubRuntime;

        var queue = [];
        var ready = false;
        var listenerBound = false;
        var waitTimer = null;
        var attempts = 0;

        function run(callback) {
            try { callback(); }
            catch (e) { log('module start failed', e); }
        }

        function flush() {
            if (ready || !window.Lampa || !window.appready) return false;

            ready = true;
            if (waitTimer) clearInterval(waitTimer);
            waitTimer = null;

            var callbacks = queue.splice(0);
            callbacks.forEach(run);
            return true;
        }

        function bindAppReady() {
            if (ready || listenerBound || !window.Lampa || !Lampa.Listener || typeof Lampa.Listener.follow !== 'function')
                return false;

            listenerBound = true;
            Lampa.Listener.follow('app', function (event) {
                if (event && event.type === 'ready') flush();
            });
            return true;
        }

        function waitForLampa() {
            if (ready || waitTimer || flush()) return;
            if (bindAppReady()) {
                flush();
                return;
            }

            waitTimer = setInterval(function () {
                attempts++;

                if (flush()) return;
                if (bindAppReady()) {
                    clearInterval(waitTimer);
                    waitTimer = null;
                    flush();
                    return;
                }

                if (attempts > 80) {
                    clearInterval(waitTimer);
                    waitTimer = null;
                    log('Lampa not found');
                }
            }, 250);
        }

        function onReady(callback) {
            if (typeof callback !== 'function') return;

            if (ready) {
                run(callback);
                return;
            }

            queue.push(callback);
            if (!flush()) waitForLampa();
        }

        function controllerName(fallback) {
            try {
                if (window.Lampa && Lampa.Controller && typeof Lampa.Controller.enabled === 'function') {
                    var enabled = Lampa.Controller.enabled();
                    var name = enabled && enabled.name ? String(enabled.name) : '';
                    if (name) return name;
                }
            } catch (e) {}
            return String(fallback || '');
        }

        function restoreController(name, fallback) {
            var target = String(name || fallback || '');
            if (!target) return;

            setTimeout(function () {
                try {
                    if (!window.Lampa || !Lampa.Controller || typeof Lampa.Controller.toggle !== 'function') return;
                    var enabled = typeof Lampa.Controller.enabled === 'function' ? Lampa.Controller.enabled() : null;
                    if (enabled && enabled.name === target) return;
                    Lampa.Controller.toggle(target);
                } catch (e) {}
            }, 0);
        }

        window.TranslationSubRuntime = {
            onReady: onReady,
            controllerName: controllerName,
            restoreController: restoreController
        };

        waitForLampa();
        return window.TranslationSubRuntime;
    }

    function openSubscriptionsPage() {
        if (!window.Lampa || !Lampa.Activity || typeof Lampa.Activity.push !== 'function') return;
        Lampa.Activity.push({ url: '', title: META.name, component: 'translationsub_list', page: 1 });
    }

    function start() {
        window.TranslationSub = {
            openSubscriptions: openSubscriptionsPage
        };

        log('plugin core started', META.version);
    }

    createRuntime().onReady(start);
})();
