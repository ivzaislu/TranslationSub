(function () {
    'use strict';

    if (window.__TranslationSubNoticeStarted) return;
    window.__TranslationSubNoticeStarted = true;

    function posterUrl(path) {
        try {
            if (window.TranslationSubUi && typeof window.TranslationSubUi.posterUrl === 'function')
                return window.TranslationSubUi.posterUrl(path);
        } catch (e) {}
        return '';
    }

    function closeAndRestore(previousController) {
        try {
            if (Lampa.Modal && typeof Lampa.Modal.close === 'function') Lampa.Modal.close();
        } catch (e) {}
        window.TranslationSubRuntime.restoreController(previousController, 'head');
    }

    function openSubscriptionsPage(previousController) {
        if (previousController) closeAndRestore(previousController);
        else {
            try {
                if (Lampa.Modal && typeof Lampa.Modal.close === 'function') Lampa.Modal.close();
            } catch (e) {}
        }

        setTimeout(function () {
            try {
                if (window.TranslationSub && typeof window.TranslationSub.openSubscriptions === 'function')
                    window.TranslationSub.openSubscriptions();
            } catch (e2) {}
        }, 0);
    }

    function openUpdate(item, previousController, directEnabled) {
        var closed = false;

        function closeOnce(restorePreviousController) {
            if (closed) return;
            closed = true;
            if (restorePreviousController) closeAndRestore(previousController);
            else {
                // Direct Online navigation immediately installs its own
                // content controller. Do not schedule restoration of the old
                // notification controller: it can steal remote focus on TV.
                try {
                    if (Lampa.Modal && typeof Lampa.Modal.close === 'function')
                        Lampa.Modal.close();
                } catch (e) {}
            }
        }

        function openSeriesCard() {
            closeOnce(true);
            setTimeout(function () {
                try {
                    var source = window.TranslationSubCardSource;
                    if (source && typeof source.open === 'function') {
                        source.open(item);
                        return;
                    }
                } catch (e) {}
                openSubscriptionsPage();
            }, 0);
        }

        // Keep the notification drawer behind the standard Lampa Select
        // chooser. Pressing Back on the remote then returns to the *same*
        // focused notification instead of dropping the user at the home page.
        try {
            var source = window.TranslationSubCardSource;
            if (directEnabled && source && typeof source.openNoticeOnline === 'function') {
                if (source.openNoticeOnline(item, {
                    beforeNavigate: function () { closeOnce(false); },
                    onFailure: openSeriesCard
                })) return;
            }
        } catch (e) {
            // Missing Online integration: retain the ordinary card route.
        }

        openSeriesCard();
    }

    function noticeCard(item, index) {
        var card;
        try {
            card = Lampa.Template.get('notice_card', {});
        } catch (e) {
            card = $('<div class="notice notice--card selector"><div class="notice__left"><div class="notice__img"><img /></div></div><div class="notice__body"><div class="notice__head"><div class="notice__title"></div><div class="notice__time"></div></div><div class="notice__descr"></div></div></div>');
        }

        item = item || {};
        var display = item.display && typeof item.display === 'object' ? item.display : {};
        var title = String(item.title || 'Сериал');
        var voice = String(item.translationName || 'Озвучка');
        var poster = posterUrl(item.poster || '');

        card.attr('data-translationsub-update', String(index));
        card.addClass('image--poster translationsub-notice__item');
        card.find('.notice__title').text(title);
        card.find('.notice__time').text(String(display.noticeTime || ''));

        var descr = card.find('.notice__descr');
        descr.empty();
        descr.append($('<div></div>').text(voice));
        if (display.noticeRange) descr.append($('<div></div>').text(String(display.noticeRange)));
        if (display.noticeSources) descr.append($('<div></div>').text(String(display.noticeSources)));

        var img = card.find('.notice__img img').first();
        if (poster && img.length) {
            img.on('load', function () { card.addClass('image--loaded'); });
            img.on('error', function () {
                try {
                    $(this).off('error');
                    this.src = './img/img_broken.svg';
                } catch (e) {}
            });
            img.attr('src', poster);
        } else {
            card.addClass('image--none image--loaded');
        }

        return card;
    }

    function emptyCard() {
        var item;
        try {
            item = Lampa.Template.get('notice_card', {});
        } catch (e) {
            item = $('<div class="notice notice--card selector"><div class="notice__body"><div class="notice__head"><div class="notice__title"></div></div><div class="notice__descr"></div></div></div>');
        }

        item.removeClass('selector focus hover');
        item.addClass('image--icon image--loaded translationsub-notice__empty');
        item.find('.notice__time').remove();
        item.find('.notice__title').text('Новых серий пока нет');
        item.find('.notice__descr').text('Когда в выбранной озвучке появится продолжение, оно будет показано здесь.');

        try { item.find('.notice__img').html(Lampa.Template.string('icon_bell_plus')); } catch (e2) {}
        return item;
    }

    function renderDrawer(updates, previousController, directEnabled) {
        updates = Array.isArray(updates) ? updates : [];

        var html = $('<div class="translationsub-notice"></div>');
        if (updates.length) updates.forEach(function (item, index) { html.append(noticeCard(item, index)); });
        else html.append(emptyCard());

        var first = updates.length ? html.find('.selector').first()[0] : null;
        try {
            Lampa.Modal.open({
                title: 'Уведомления озвучек',
                size: 'medium',
                html: html,
                select: first,
                scroll_to_center: true,
                buttons: [{
                    name: 'Все подписки на озвучки',
                    onSelect: function () { openSubscriptionsPage(previousController); }
                }],
                buttons_position: 'inside',
                onSelect: function (selected) {
                    var node = $(selected);
                    var index = parseInt(node.attr('data-translationsub-update'), 10);
                    if (isNaN(index) || !updates[index]) return;
                    openUpdate(updates[index], previousController, directEnabled);
                },
                onBack: function () { closeAndRestore(previousController); }
            });
        } catch (e) {
            window.TranslationSubRuntime.restoreController(previousController, 'head');
        }
    }

    function openDrawer(preloadedUpdates, directEnabled) {
        if (!window.Lampa || !Lampa.Modal || typeof Lampa.Modal.open !== 'function') {
            openSubscriptionsPage();
            return;
        }

        var previousController = window.TranslationSubRuntime.controllerName('head');
        renderDrawer(Array.isArray(preloadedUpdates) ? preloadedUpdates : [], previousController, !!directEnabled);
    }

    function start() {
        window.TranslationSubNotice = { open: openDrawer };
    }

    window.TranslationSubRuntime.onReady(start);
})();
