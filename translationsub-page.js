(function () {
    'use strict';

    if (window.__TranslationSubPageStarted) return;

    function escapeHtml(value) {
        try {
            if (Lampa.Utils && typeof Lampa.Utils.escape === 'function') return Lampa.Utils.escape(String(value || ''));
        } catch (e) {}

        return String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/\"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function notify(text) {
        try {
            if (Lampa.Noty && typeof Lampa.Noty.show === 'function') Lampa.Noty.show(text);
        } catch (e) {}
    }

    function posterUrl(path) {
        try {
            if (window.TranslationSubUi && typeof window.TranslationSubUi.posterUrl === 'function')
                return window.TranslationSubUi.posterUrl(path);
        } catch (e) {}
        return '';
    }

    function metaHtml(item) {
        item = item || {};
        var display = item.display && typeof item.display === 'object' ? item.display : {};
        var schedule = item.schedule && typeof item.schedule === 'object' ? item.schedule : null;
        var progress = Math.max(0, Math.min(100, Number(item.progressPercent || 0) || 0));
        var body = '<div class="translationsub-meta-v2">' +
            '<div class="translationsub-meta-v2__row">' +
                '<span class="translationsub-meta-v2__season">' + escapeHtml(display.season || '') + '</span>' +
                '<span class="translationsub-meta-v2__pill">' + escapeHtml(display.watched || '') + '</span>' +
                '<span class="translationsub-meta-v2__pill">' + escapeHtml(display.available || '') + '</span>' +
            '</div>' +
            '<div class="translationsub-progress-v2"><i style="width:' + progress + '%"></i></div>';

        if (display.progress) {
            body += '<div class="' + (item.hasNewEpisodes ? 'translationsub-meta-v2__next' : 'translationsub-meta-v2__ok') + '">' +
                escapeHtml(display.progress) +
            '</div>';
        }

        if (display.tmdbFacts || display.tmdbNext || (schedule && schedule.text)) {
            var title = display.tmdbTitle ? ' title="' + escapeHtml(display.tmdbTitle) + '"' : '';
            body += '<div class="translationsub-tmdb-v2"' + title + '>';

            if (display.tmdbFacts || display.tmdbNext) {
                var facts = [];
                if (display.tmdbFacts)
                    facts.push('<span class="translationsub-tmdb-v2__air">' + escapeHtml(display.tmdbFacts) + '</span>');
                if (display.tmdbNext)
                    facts.push('<span class="translationsub-tmdb-v2__next">' + escapeHtml(display.tmdbNext) + '</span>');
                body += '<div class="translationsub-tmdb-v2__facts">' +
                    facts.join('<span class="translationsub-tmdb-v2__sep">•</span>') +
                '</div>';
            }

            if (schedule && schedule.text) {
                var type = String(schedule.type || 'plain').replace(/[^a-z0-9-]/gi, '');
                body += '<div class="translationsub-tmdb-v2__state translationsub-tmdb-v2__state--' + type + '">' +
                    escapeHtml(schedule.text) +
                '</div>';
            }

            body += '</div>';
        }

        return body + '</div>';
    }

    function SubscriptionPage() {
        var scroll = new Lampa.Scroll({ mask: true, over: true });
        var html = $('<div class="translationsub-page"></div>');
        var self = this;
        var initialized = false;
        var destroyed = false;
        var unsubscribeState = null;
        var hasSnapshot = false;
        var latestSnapshot = null;
        var checkBusy = false;

        function pageActive() {
            if (destroyed) return false;
            try {
                var active = Lampa.Activity.active();
                return !!active && active.activity === self.activity;
            } catch (e) {
                return false;
            }
        }

        function controllerName() {
            try {
                if (window.TranslationSubRuntime && typeof window.TranslationSubRuntime.controllerName === 'function')
                    return window.TranslationSubRuntime.controllerName('');
            } catch (e) {}
            try {
                if (Lampa.Controller && typeof Lampa.Controller.enabled === 'function') {
                    var enabled = Lampa.Controller.enabled();
                    return enabled && enabled.name ? String(enabled.name) : '';
                }
            } catch (e2) {}
            return '';
        }

        function canStealFocus() {
            return controllerName() === 'content';
        }

        this.create = function () {
            scroll.minus();
            scroll.append(html);
            return this.render();
        };

        this.render = function () { return scroll.render(); };
        this.pause = function () {};
        this.stop = function () {};
        this.back = function () { Lampa.Activity.backward(); };

        this.start = function () {
            if (!pageActive()) return;

            Lampa.Controller.add('content', {
                toggle: function () {
                    if (!pageActive()) return;
                    Lampa.Controller.collectionSet(scroll.render());
                    var focused = html.find('.selector.focus')[0] || html.find('.selector')[0];
                    if (focused) Lampa.Controller.collectionFocus(focused, scroll.render());
                },
                up: function () {
                    if (Navigator.canmove('up')) Navigator.move('up');
                    else Lampa.Controller.toggle('head');
                },
                down: function () { Navigator.move('down'); },
                left: function () {
                    if (Navigator.canmove('left')) Navigator.move('left');
                    else Lampa.Controller.toggle('menu');
                },
                right: function () { Navigator.move('right'); },
                back: this.back
            });

            Lampa.Controller.toggle('content');

            if (!initialized) {
                initialized = true;
                this.initialize();
            } else if (latestSnapshot) {
                render(latestSnapshot);
            }
        };

        this.destroy = function () {
            destroyed = true;
            if (typeof unsubscribeState === 'function') {
                try { unsubscribeState(); } catch (e) {}
            }
            unsubscribeState = null;
            latestSnapshot = null;
            try { scroll.destroy(); } catch (e2) {}
            html.remove();
        };

        function focusAfterRender(subscriptionId) {
            setTimeout(function () {
                if (!pageActive() || !canStealFocus()) return;
                try {
                    Lampa.Controller.collectionSet(scroll.render());
                    var target = null;
                    if (subscriptionId) {
                        html.find('.translationsub-card').each(function () {
                            if (!target && String($(this).attr('data-subscription-id') || '') === String(subscriptionId))
                                target = this;
                        });
                    }
                    if (!target) target = html.find('.selector')[0];
                    if (target) Lampa.Controller.collectionFocus(target, scroll.render());
                } catch (e) {}
            }, 40);
        }

        function render(snapshot) {
            if (!pageActive()) return;
            snapshot = snapshot && typeof snapshot === 'object' ? snapshot : {};
            var list = Array.isArray(snapshot.subscriptions) ? snapshot.subscriptions : [];
            var updateCount = snapshot.badge ? Number(snapshot.badge.count || 0) || 0 : 0;

            var focusedId = String(html.find('.translationsub-card.focus').attr('data-subscription-id') || '');
            html.empty();

            var summary = $('<div class="translationsub-summary-v2"></div>');
            summary.append('<div class="translationsub-summary-v2__item"><span class="translationsub-summary-v2__dot"></span><span>Подписок <b>' + list.length + '</b></span></div>');
            summary.append('<div class="translationsub-summary-v2__item translationsub-summary-v2__item--new"><span class="translationsub-summary-v2__dot"></span><span>С новыми сериями <b>' + updateCount + '</b></span></div>');

            var toolbar = $('<div class="translationsub-toolbar"></div>');
            var check = $('<div class="translationsub-toolbar__item selector"><span>Проверить новые серии</span></div>');
            check.on('hover:enter', function () {
                if (checkBusy) return;

                var api = window.TranslationSubApi;
                var badge = window.TranslationSubBadgeState;
                if (!api || typeof api.check !== 'function' || !badge || typeof badge.applyCommand !== 'function') {
                    notify('TranslationSub API недоступен');
                    return;
                }

                checkBusy = true;
                notify('Проверяю новые серии…');
                api.check(function (result) {
                    checkBusy = false;
                    var applied = badge.applyCommand(result);
                    if (!applied) {
                        notify('Не удалось проверить новые серии');
                        return;
                    }

                    var count = Number(applied.count || 0) || 0;
                    notify(count ? ('С новыми сериями: ' + count) : 'Новых серий нет');
                }, function () {
                    checkBusy = false;
                    notify('Не удалось проверить новые серии');
                });
            });
            toolbar.append(check);

            var top = $('<div class="translationsub-page__top"></div>');
            top.append(toolbar);
            top.append(summary);
            html.append(top);

            var container = $('<div class="translationsub-list"></div>');
            if (!list.length) {
                container.append('<div class="translationsub-empty">Подписок пока нет. Откройте сериал, нажмите «Озвучки» и выберите нужную озвучку.</div>');
            }

            list.forEach(function (item) {
                item = item || {};
                var display = item.display && typeof item.display === 'object' ? item.display : {};
                var id = String(item.id || '');
                var title = String(item.title || 'Без названия');
                var voice = String(item.translationName || 'Озвучка');
                var poster = posterUrl(item.poster || '');
                var posterHtml = poster
                    ? '<img src="' + escapeHtml(poster) + '" alt="">'
                    : '<div class="translationsub-card__poster-empty"></div>';

                var card = $('<div class="translationsub-card selector' + (item.hasNewEpisodes ? ' translationsub-card--has-new' : '') + '" data-subscription-id="' + escapeHtml(id) + '">' +
                    '<div class="translationsub-card__poster">' + posterHtml + '</div>' +
                    '<div class="translationsub-card__body">' +
                        '<div class="translationsub-card__heading">' +
                            '<div class="translationsub-card__title">' + escapeHtml(title) + '</div>' +
                            '<div class="translationsub-card__voice">' + escapeHtml(voice) + '</div>' +
                        '</div>' +
                        '<div class="translationsub-card__meta">' + metaHtml(item) + '</div>' +
                    '</div>' +
                    (display.newBadge ? '<div class="translationsub-card__new">' + escapeHtml(display.newBadge) + '</div>' : '') +
                '</div>');

                card.on('hover:focus', function (event) {
                    try { scroll.update($(event.target), true); } catch (e) {}
                });
                container.append(card);
            });

            html.append(container);

            try {
                if (window.TranslationSubCardSource && typeof window.TranslationSubCardSource.bindPage === 'function')
                    window.TranslationSubCardSource.bindPage(html, list);
            } catch (e) {}

            try {
                if (window.TranslationSubUi && typeof window.TranslationSubUi.refresh === 'function')
                    window.TranslationSubUi.refresh();
            } catch (e2) {}

            if (!pageActive()) return;
            if (!canStealFocus()) return;
            Lampa.Controller.enable('content');
            focusAfterRender(focusedId);
        }

        function consumeSnapshot(snapshot) {
            if (destroyed || !snapshot || typeof snapshot !== 'object') return;
            var first = !hasSnapshot;
            hasSnapshot = true;
            latestSnapshot = snapshot;
            try { self.activity.loader(false); } catch (e) {}
            if (!pageActive()) return;
            render(snapshot);
            if (first && canStealFocus()) {
                try { self.activity.toggle(); } catch (e2) {}
            }
        }

        function bindState() {
            var badge = window.TranslationSubBadgeState;
            if (!badge || typeof badge.subscribe !== 'function') return false;

            unsubscribeState = badge.subscribe(function (value) {
                if (value && value.snapshot) consumeSnapshot(value.snapshot);
            });
            return true;
        }

        this.initialize = function () {
            var badge = window.TranslationSubBadgeState;
            if (!bindState() || !badge || typeof badge.refresh !== 'function') {
                if (pageActive()) {
                    render({ subscriptions: [], badge: { count: 0 } });
                    if (canStealFocus()) {
                        try { self.activity.toggle(); } catch (e) {}
                    }
                    notify('TranslationSub state недоступен');
                }
                return;
            }

            if (hasSnapshot) return;

            try { self.activity.loader(true); } catch (e) {}
            badge.refresh(function () {
                if (destroyed || hasSnapshot || !pageActive()) return;
                try { self.activity.loader(false); } catch (e) {}
                render({ subscriptions: [], badge: { count: 0 } });
                if (canStealFocus()) {
                    try { self.activity.toggle(); } catch (e2) {}
                }
                notify('Не удалось загрузить подписки');
            });
        };
    }

    function register() {
        if (window.__TranslationSubPageStarted) return;
        Lampa.Component.add('translationsub_list', SubscriptionPage);
        window.__TranslationSubPageStarted = true;
    }

    window.TranslationSubRuntime.onReady(register);
})();
