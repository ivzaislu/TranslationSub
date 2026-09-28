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

    function openUpdate(item, previousController) {
        closeAndRestore(previousController);
        setTimeout(function () {
            try {
                if (window.TranslationSubCardSource && typeof window.TranslationSubCardSource.open === 'function') {
                    window.TranslationSubCardSource.open(item);
                    return;
                }
            } catch (e) {}
            openSubscriptionsPage();
        }, 0);
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

    function renderDrawer(updates, previousController) {
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
                    openUpdate(updates[index], previousController);
                },
                onBack: function () { closeAndRestore(previousController); }
            });
        } catch (e) {
            window.TranslationSubRuntime.restoreController(previousController, 'head');
        }
    }

    function openDrawer(preloadedUpdates) {
        if (!window.Lampa || !Lampa.Modal || typeof Lampa.Modal.open !== 'function') {
            openSubscriptionsPage();
            return;
        }

        var previousController = window.TranslationSubRuntime.controllerName('head');
        renderDrawer(Array.isArray(preloadedUpdates) ? preloadedUpdates : [], previousController);
    }

    function start() {
        window.TranslationSubNotice = { open: openDrawer };
    }

    window.TranslationSubRuntime.onReady(start);
})();
