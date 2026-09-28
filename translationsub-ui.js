(function () {
    'use strict';

    if (window.__TranslationSubUiV2Started) return;
    window.__TranslationSubUiV2Started = true;

    var lampaBound = false;
    var BELL_BODY = 'M12 3.5a5.5 5.5 0 0 0-5.5 5.5v3.2c0 1.9-.7 3.5-2.1 4.8l-.9.8h17l-.9-.8c-1.4-1.3-2.1-2.9-2.1-4.8V9A5.5 5.5 0 0 0 12 3.5Z';
    var BELL_CLAPPER = 'M9.6 20h4.8c-.35 1.1-1.25 1.7-2.4 1.7S9.95 21.1 9.6 20Z';

    function layoutMode() {
        try {
            if (window.Lampa && Lampa.Platform && typeof Lampa.Platform.screen === 'function') {
                if (Lampa.Platform.screen('mobile')) return 'mobile';
                if (Lampa.Platform.screen('tv')) return 'tv';
            }
        } catch (e) {}

        try {
            var ua = String(navigator.userAgent || '').toLowerCase();
            if (/android tv|google tv|googletv|mibox|mitv|smarttv|smart tv|tizen|webos/.test(ua)) return 'tv';
            if (/iphone|ipad|android/.test(ua) && ('ontouchstart' in window || navigator.maxTouchPoints > 0)) return 'mobile';
        } catch (e2) {}
        return 'desktop';
    }

    function posterUrl(path) {
        path = String(path || '');
        if (!path) return '';
        if (/^https?:\/\//i.test(path)) return path;

        try {
            if (window.Lampa && Lampa.Api && typeof Lampa.Api.img === 'function')
                return Lampa.Api.img(path, 'w300');
        } catch (e) {}

        try {
            if (window.Lampa && Lampa.TMDB && typeof Lampa.TMDB.image === 'function')
                return Lampa.TMDB.image('t/p/w300/' + path.replace(/^\//, ''));
        } catch (e2) {}

        return path.charAt(0) === '/' ? 'https://image.tmdb.org/t/p/w300' + path : path;
    }

    function maskSvg(filled) {
        return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">' +
            '<path d="' + BELL_BODY + '" fill="' + (filled ? 'white' : 'none') + '" stroke="white" stroke-width="1.8" stroke-linejoin="round"/>' +
            '<path d="' + BELL_CLAPPER + '" fill="white"/>' +
        '</svg>';
    }

    function maskUrl(filled) {
        return 'url("data:image/svg+xml,' + encodeURIComponent(maskSvg(filled)) + '")';
    }

    function injectStyles() {
        if (document.getElementById('translationsub-ui-v2-style')) return;

        var outline = maskUrl(false);
        var filled = maskUrl(true);
        var style = document.createElement('style');
        style.id = 'translationsub-ui-v2-style';
        style.textContent = [
            '.translationsub-page{width:100%;padding:1.5em 1.5em 5em;box-sizing:border-box}',
            '.translationsub-page__top{display:flex;align-items:center;gap:1em;flex-wrap:wrap;margin:0 0 1.5em}',
            '.translationsub-page__top .translationsub-toolbar,.translationsub-page__top .translationsub-summary-v2{margin:0}',
            '.translationsub-summary-v2{display:flex;align-items:center;gap:.6em;flex-wrap:wrap}',
            '.translationsub-summary-v2__item{display:flex;align-items:center;gap:.5em;padding:.45em .75em;border-radius:1em;background:rgba(0,0,0,.3);font-size:.9em;line-height:1.3}',
            '.translationsub-summary-v2__item b{font-weight:600}',
            '.translationsub-summary-v2__dot{width:.6em;height:.6em;border-radius:100%;background:#fff;opacity:.7}',
            '.translationsub-summary-v2__item--new .translationsub-summary-v2__dot{background:#d9822b;opacity:1}',
            '.translationsub-toolbar{display:flex;gap:.75em;flex-wrap:wrap}',
            '.translationsub-toolbar__item{min-height:2.8em;box-sizing:border-box;padding:.55em 1em;border-radius:1em;background:rgba(0,0,0,.3);font-size:1em;display:flex;align-items:center;transition:background-color .2s,color .2s}',
            '.translationsub-toolbar__item.focus{background:#fff;color:#000}',
            '.translationsub-list,.translationsub-layout--tv .translationsub-list,.translationsub-layout--desktop .translationsub-list{display:block}',
            '.translationsub-card{display:flex;align-items:stretch;height:11.25em;min-height:11.25em;margin:0 0 1.15em;border-radius:1em;background:rgba(0,0,0,.3);overflow:hidden;position:relative;transition:box-shadow .2s,background-color .2s;transform:translateZ(0)}',
            '.translationsub-card.focus{box-shadow:0 0 0 .3em #fff;z-index:3}',
            '.translationsub-card.hover{box-shadow:0 0 0 .3em rgba(255,255,255,.5)}',
            '.translationsub-card__poster{width:7.5em;min-width:7.5em;height:100%;flex:0 0 7.5em;align-self:stretch;position:relative;background:#242424;overflow:hidden;display:flex;align-items:center;justify-content:center}',
            '.translationsub-card__poster img{width:100%;height:100%;object-fit:contain;display:block;background:#242424}',
            '.translationsub-card__poster-empty{width:100%;height:100%;background:#242424}',
            '.translationsub-card__body{padding:.75em 1.1em;min-width:0;display:flex;flex-direction:column;flex:1;justify-content:flex-start;overflow:hidden;box-sizing:border-box}',
            '.translationsub-card__heading{display:flex;align-items:center;gap:.65em;min-width:0}',
            '.translationsub-card--has-new .translationsub-card__heading{padding-right:5.6em}',
            '.translationsub-card__title{font-size:1.3em;line-height:1.2;font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;min-width:0;flex:1 1 auto}',
            '.translationsub-card__voice{display:block;flex:0 1 auto;max-width:34%;padding:.3em .6em;border-radius:1em;background:rgba(0,0,0,.3);font-size:.85em;font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
            '.translationsub-card__meta{margin-top:.55em;font-size:.88em;min-width:0}',
            '.translationsub-meta-v2{display:flex;flex-direction:column;gap:.35em;min-width:0}',
            '.translationsub-meta-v2__row{display:flex;align-items:center;gap:.45em;flex-wrap:nowrap;min-width:0}',
            '.translationsub-meta-v2__season{font-weight:600;flex:0 0 auto;font-size:1.02em}',
            '.translationsub-meta-v2__pill{padding:.22em .5em;border-radius:1em;background:rgba(255,255,255,.1);opacity:.8;white-space:nowrap}',
            '.translationsub-progress-v2{height:.3em;margin:.05em 0;border-radius:1em;background:rgba(255,255,255,.1);overflow:hidden}',
            '.translationsub-progress-v2>i{display:block;height:100%;border-radius:inherit;background:#c9a15b}',
            '.translationsub-meta-v2__next{font-size:.95em;line-height:1.25;font-weight:400;color:#d8c39a}',
            '.translationsub-meta-v2__ok{font-size:.93em;line-height:1.25;opacity:.6}',
            '.translationsub-tmdb-v2{margin-top:.15em;padding-top:.4em;border-top:1px solid rgba(255,255,255,.1);font-size:.82em;line-height:1.25;min-width:0}',
            '.translationsub-tmdb-v2__facts{display:flex;align-items:center;gap:.35em;flex-wrap:nowrap;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;opacity:.6}',
            '.translationsub-tmdb-v2__facts>*{flex:0 0 auto}',
            '.translationsub-tmdb-v2__sep{opacity:.45}',
            '.translationsub-tmdb-v2__state{margin-top:.15em;font-size:.96em;line-height:1.2;font-weight:400;opacity:.75;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
            '.translationsub-tmdb-v2__state--active,.translationsub-tmdb-v2__state--new-season{color:#d8c39a;opacity:1}',
            '.translationsub-tmdb-v2__state--ok{color:#b8b39a;opacity:1}',
            '.translationsub-tmdb-v2__state--warn{color:#d8b56a;opacity:1}',
            '.translationsub-card__new{position:absolute;right:.7em;top:.7em;padding:.35em .7em;border-radius:5em;background:#d9822b;color:#241508;border:1px solid rgba(255,205,145,.28);font-size:.7em;font-weight:600}',
            '.translationsub-empty{padding:3em 1.5em;border-radius:.3em;background:rgba(0,0,0,.3);font-size:1.1em;line-height:1.5;font-weight:300}',
            '@supports(display:grid){.translationsub-list,.translationsub-layout--tv .translationsub-list,.translationsub-layout--desktop .translationsub-list{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.15em 1.25em;align-items:stretch}.translationsub-card{margin:0}.translationsub-empty{grid-column:1/-1}}',
            'body.size--bigger .translationsub-card{font-size:1.14em}',
            '.translationsub-layout--tv{width:100%;padding:1.5em 1.5em 5em}',
            '.translationsub-layout--tv .translationsub-card{height:11.025em;min-height:11.025em}',
            '.translationsub-layout--tv .translationsub-card__poster{width:7.35em;min-width:7.35em;flex-basis:7.35em}',
            '.translationsub-layout--tv .translationsub-card__body{padding:.75em 1.1em}',
            '.translationsub-layout--mobile{width:100%;padding:1em 1em calc(8em + env(safe-area-inset-bottom,0px))}',
            '.translationsub-layout--mobile .translationsub-page__top{display:grid;grid-template-columns:1fr;gap:.75em;margin-bottom:1em}',
            '.translationsub-layout--mobile .translationsub-toolbar{width:100%}',
            '.translationsub-layout--mobile .translationsub-toolbar__item{width:100%;justify-content:center;min-height:2.8em}',
            '.translationsub-layout--mobile .translationsub-summary-v2{width:100%;gap:.5em}',
            '.translationsub-layout--mobile .translationsub-summary-v2__item{font-size:.82em;padding:.4em .65em}',
            '.translationsub-layout--mobile .translationsub-list{grid-template-columns:1fr;gap:.9em}',
            '.translationsub-layout--mobile .translationsub-card{height:9.3em;min-height:9.3em}',
            '.translationsub-layout--mobile .translationsub-card__poster{width:6.2em;min-width:6.2em;flex-basis:6.2em}',
            '.translationsub-layout--mobile .translationsub-card__body{padding:.6em .85em}',
            '.translationsub-layout--mobile .translationsub-card__heading{gap:.45em}',
            '.translationsub-layout--mobile .translationsub-card--has-new .translationsub-card__heading{padding-right:4.8em}',
            '.translationsub-layout--mobile .translationsub-card__title{font-size:1.08em}',
            '.translationsub-layout--mobile .translationsub-card__voice{font-size:.76em;max-width:38%;padding:.25em .5em}',
            '.translationsub-layout--mobile .translationsub-card__meta{font-size:.78em;margin-top:.4em}',
            '.translationsub-layout--mobile .translationsub-meta-v2{gap:.25em}',
            '.translationsub-layout--mobile .translationsub-tmdb-v2{font-size:.74em;padding-top:.3em;margin-top:.1em}',
            '.translationsub-layout--mobile .translationsub-tmdb-v2__facts{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
            '.translationsub-layout--mobile .translationsub-card__new{right:.55em;top:.55em;font-size:.64em}',
            '.translationsub-full-button--mobile>span{display:none!important}',
            '.translationsub-full-button--mobile{touch-action:manipulation;-webkit-tap-highlight-color:transparent}',
            '@media(max-width:390px){.translationsub-layout--mobile .translationsub-card{height:8.4em;min-height:8.4em}.translationsub-layout--mobile .translationsub-card__poster{width:5.6em;min-width:5.6em;flex-basis:5.6em}.translationsub-layout--mobile .translationsub-card__body{padding:.5em .7em}.translationsub-layout--mobile .translationsub-meta-v2__row{flex-wrap:wrap}.translationsub-layout--mobile .translationsub-card__title{font-size:1em}}',
            'body.light--version .translationsub-card{border-radius:0}',
            'body.light--version .translationsub-card.focus,body.light--version .translationsub-card.hover{border-radius:.4em}',
            'body.light--version .translationsub-toolbar__item{border-radius:.2em}',
            'body.light--version .translationsub-card__voice,body.light--version .translationsub-meta-v2__pill,body.light--version .translationsub-empty{border-radius:.3em}',
            'body.glass--style .translationsub-card,body.glass--style .translationsub-toolbar__item,body.glass--style .translationsub-summary-v2__item,body.glass--style .translationsub-empty{background-color:rgba(70,70,70,.3)}',
            'body.glass--style.platform--browser .translationsub-card,body.glass--style.platform--nw .translationsub-card,body.glass--style.platform--apple .translationsub-card,body.glass--style.platform--apple_tv .translationsub-card{backdrop-filter:blur(1em)}',
            '.translationsub-state-badge{position:absolute;right:-.25em;top:-.25em;min-width:1.5em;height:1.5em;padding:0 .3em;border-radius:1em;background:#d9822b;color:#241508;font-size:.7em;font-weight:600;display:flex;align-items:center;justify-content:center;box-sizing:border-box;pointer-events:none}',
            '.translationsub-state-menu-badge{margin-left:auto;min-width:1.65em;height:1.65em;padding:0 .38em;border-radius:1em;background:#d9822b;color:#241508;font-size:.7em;font-weight:600;display:flex;align-items:center;justify-content:center;box-sizing:border-box;pointer-events:none}',
            '.translationsub-menu-item.focus .translationsub-state-menu-badge{background:#000;color:#fff}',
            '.translationsub-head{position:relative;display:flex;align-items:center;justify-content:center}',
            '.translationsub-full-button:before{display:none!important}',
            '.translationsub-full-button>.translationsub-full-button__bell{display:block;width:1.5em;height:1.5em;flex:0 0 1.5em;background:' + outline + ' center/contain no-repeat;fill:none;stroke:none}',
            '.translationsub-full-button--subscribed>.translationsub-full-button__bell{background-image:' + filled + '}',
            '.translationsub-head>svg{display:block;width:1.8em;height:1.8em;background:' + outline + ' center/contain no-repeat;fill:none;stroke:none}',
            '.translationsub-head.translationsub-head--has-updates>svg{background-image:' + filled + '}',
            '.translationsub-menu-item .menu__ico svg{display:block;width:100%;height:100%;background:' + outline + ' center/contain no-repeat;fill:none;stroke:none}',
            '.settings-folder[data-component="translationsub_settings"] .translationsub-settings-bell{width:2em;height:2em;transform:scale(1.35);transform-origin:50% 50%;overflow:visible;background:' + outline + ' center/contain no-repeat;fill:none;stroke:none}',
            '.translationsub-notice{padding:0}',
            '.translationsub-notice__item{border:1px solid rgba(255,255,255,.14);border-radius:.8em;box-sizing:border-box;overflow:hidden;margin-bottom:.85em}',
            '.translationsub-notice__item:last-child{margin-bottom:0}',
            '.translationsub-notice__item.focus{border-color:rgba(255,255,255,.48)}',
            '.translationsub-notice .notice__img{background:#242424}',
            '.translationsub-notice .notice__img img{width:100%;height:100%;object-fit:contain;background:#242424}',
            '.translationsub-notice .notice__time{padding:.25em .65em;border-radius:1em;background:#d9822b;color:#241508;border:1px solid rgba(255,205,145,.28);font-weight:600;opacity:1}',
            '.translationsub-notice__empty{opacity:.72}',
            '.translationsub-notice__empty .notice__img{position:relative}',
            '.translationsub-notice__empty .notice__img>*{display:none}',
            '.translationsub-notice__empty .notice__img:before{content:"";display:block;width:3.5em;height:3.5em;margin:auto;background:' + outline + ' center/contain no-repeat}',
            '@supports ((-webkit-mask-image:url("")) or (mask-image:url(""))){.translationsub-full-button>.translationsub-full-button__bell{background:currentColor;-webkit-mask:' + outline + ' center/contain no-repeat;mask:' + outline + ' center/contain no-repeat}.translationsub-full-button--subscribed>.translationsub-full-button__bell{-webkit-mask:' + filled + ' center/contain no-repeat;mask:' + filled + ' center/contain no-repeat}.translationsub-head>svg{background:currentColor;-webkit-mask:' + outline + ' center/contain no-repeat;mask:' + outline + ' center/contain no-repeat}.translationsub-head.translationsub-head--has-updates>svg{-webkit-mask:' + filled + ' center/contain no-repeat;mask:' + filled + ' center/contain no-repeat}.translationsub-menu-item .menu__ico svg{background:currentColor;-webkit-mask:' + outline + ' center/contain no-repeat;mask:' + outline + ' center/contain no-repeat}.settings-folder[data-component="translationsub_settings"] .translationsub-settings-bell{background:currentColor;-webkit-mask:' + outline + ' center/contain no-repeat;mask:' + outline + ' center/contain no-repeat}.translationsub-notice__empty .notice__img:before{background:currentColor;-webkit-mask:' + outline + ' center/contain no-repeat;mask:' + outline + ' center/contain no-repeat}}',
            '.translationsub-full-button{position:relative}',
            '.translationsub-full-button[data-translationsub-card-flow="1"] span{white-space:nowrap}'
        ].join('');

        (document.head || document.documentElement).appendChild(style);
    }

    function applyFullButtonMode() {
        if (typeof $ !== 'function') return;
        var mobile = layoutMode() === 'mobile';
        $('.translationsub-full-button').each(function () {
            $(this).toggleClass('translationsub-full-button--mobile', mobile);
        });
    }

    function applyLayout(page) {
        if (typeof $ !== 'function') return;
        var root = $(page);
        var mode = layoutMode();
        root.removeClass('translationsub-layout--tv translationsub-layout--mobile translationsub-layout--desktop');
        root.addClass('translationsub-layout--' + mode);
    }

    function refresh() {
        if (typeof $ !== 'function') return;
        $('.translationsub-page').each(function () { applyLayout(this); });
        applyFullButtonMode();
    }

    function bindLampa() {
        if (lampaBound) return true;
        try {
            if (!Lampa.Listener || typeof Lampa.Listener.follow !== 'function') return false;
            Lampa.Listener.follow('full', function (event) {
                if (event && event.type === 'complite') refresh();
            });
            lampaBound = true;
            return true;
        } catch (e) {
            return false;
        }
    }

    function start() {
        injectStyles();
        bindLampa();
        window.TranslationSubUi = { refresh: refresh, posterUrl: posterUrl };
        refresh();
        try {
            window.addEventListener('resize', refresh);
            window.addEventListener('orientationchange', refresh);
        } catch (e) {}
    }

    window.TranslationSubRuntime.onReady(start);
})();