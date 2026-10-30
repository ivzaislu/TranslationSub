#!/usr/bin/env node
'use strict';

// Deterministic client tests for the Lampac integration (no live Lampa needed).
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '../../translationsub-source.js'), 'utf8');
const notice = fs.readFileSync(path.resolve(__dirname, '../../translationsub-notice.js'), 'utf8');

function makeClient() {
    const storage = {};
    const activities = [];
    const selections = [];
    let selectorCloses = 0;
    const client = {
        lampac_plugin: true,
        TranslationSubRuntime: {
            onReady(callback) { callback(); },
            restoreController() {},
            controllerName() { return 'head'; }
        }
    };
    const Lampa = {
        Storage: {
            cache(key, expiry, fallback) { return storage[key] || fallback; },
            set(key, value) { storage[key] = JSON.parse(JSON.stringify(value)); },
            get() { return ''; }
        },
        Activity: {
            push(value) { activities.push(value); },
            back() { activities.pop(); }
        },
        Select: {
            show(options) { selections.push(options); },
            close() { selectorCloses++; }
        },
        Noty: { show() {} }
    };
    client.Lampa = Lampa;
    const context = vm.createContext({
        window: client,
        Lampa,
        setTimeout(callback) { callback(); },
        console
    });
    vm.runInContext(source, context, { filename: 'translationsub-source.js' });
    assert.equal(typeof client.TranslationSubCardSource.openNoticeOnline, 'function');
    return { client, storage, activities, selections, context, selectorCloses: () => selectorCloses };
}

function item(sources) {
    return {
        id: 'subscription-1',
        navigation: { id: '123', method: 'tv' },
        tmdbId: '123',
        title: 'Example',
        originalTitle: 'Example Original',
        year: 2026,
        imdbId: 'tt1234567',
        season: 2,
        fromEpisode: 3,
        toEpisode: 3,
        availableEpisode: 3,
        translationId: 'voice-shared',
        translationName: 'LostFilm',
        sources
    };
}

const active = { source: 'collaps', latestEpisode: 3, translationId: 'voice-collaps', translationName: 'LostFilm' };
const stale = { source: 'hdvb', latestEpisode: 2, translationId: 'voice-hdvb', translationName: 'LostFilm' };
const activeOther = { ...stale, latestEpisode: 3 };

{
    const { client, activities, storage, selections } = makeClient();
    storage.online_choice_collaps = { 123: { episodes_view: { 1: 2 }, voice_name: 'Wrong' } };
    assert.equal(client.TranslationSubCardSource.openNoticeOnline(item([active, stale])), true);
    assert.equal(activities.length, 2);
    assert.equal(selections.length, 0);
    assert.equal(activities[0].component, 'full', 'only direct mode inserts a series card underneath Online');
    assert.equal(activities[0].source, 'tmdb');
    assert.equal(activities[0].id, 123);
    assert.equal(activities[0].method, 'tv');
    assert.equal(activities[1].component, 'lampac');
    assert.equal(activities[1].lampac_custom_select, 'collaps');
    assert.equal(activities[1].movie.id, 123);
    assert.equal(activities[1].movie.name, 'Example');
    // Lampa Explorer handles poster OK by Activity.back() when there is a
    // previous activity. The card underneath must be the requested series.
    client.Lampa.Activity.back();
    assert.equal(activities.length, 1);
    assert.equal(activities[0].component, 'full');
    assert.equal(activities[0].id, 123);
    assert.equal(storage.online_choice_collaps[123].season, 1);
    assert.equal(storage.online_choice_collaps[123].voice_name, 'LostFilm');
    assert.equal(storage.online_choice_collaps[123].voice_url, '');
    assert.equal(storage.online_choice_collaps[123].episodes_view[1], 2);
    console.log('PASS: one active balancer -> Online above correct series card; poster OK returns to card');
}

{
    const { client, activities, selections, selectorCloses } = makeClient();
    const steps = [];
    assert.equal(client.TranslationSubCardSource.openNoticeOnline(item([active, activeOther]), {
        beforeNavigate() { steps.push('close modal'); },
        onFailure() { steps.push('fallback'); }
    }), true);
    assert.equal(activities.length, 0, 'must not arbitrarily select between active balancers');
    assert.equal(selections.length, 1);
    assert.equal(selections[0].items.length, 2);
    assert.equal(steps.length, 0, 'keep modal open while remote selector is displayed');
    assert.equal(selections[0].onBack, undefined, 'let Lampa restore the original modal and its focus');
    selections[0].onSelect(selections[0].items[1]);
    assert.equal(activities.length, 2);
    assert.equal(activities[0].component, 'full');
    assert.equal(activities[1].component, 'lampac');
    assert.equal(activities[1].lampac_custom_select, 'hdvb');
    assert.deepEqual(steps, ['close modal']);
    assert.equal(selectorCloses(), 0, 'Lampa Select closes itself before onSelect');
    console.log('PASS: two balancers -> TV remote menu without duplicate close/focus loss');
}

{
    const { client, activities, selections } = makeClient();
    assert.equal(client.TranslationSubCardSource.openNoticeOnline(item([stale])), false);
    assert.equal(client.TranslationSubCardSource.openNoticeOnline(item([{
        source: 'collaps', latestEpisode: 0, translationName: 'LostFilm'
    }])), false);
    assert.equal(activities.length, 0);
    assert.equal(selections.length, 0);
    console.log('PASS: missing/legacy episode evidence never starts direct playback');
}

{
    const { client, activities } = makeClient();
    client.lampac_plugin = false;
    assert.equal(client.TranslationSubCardSource.openNoticeOnline(item([active])), false);
    assert.equal(activities.length, 0);
    console.log('PASS: missing Lampac Online plugin triggers fallback');
}

// A minimal DOM stub exercises the real notice -> Lampa.Select ->
// Lampac Activity path without a browser, using Lampa's own selection hooks.
function makeNoticeClient() {
    const test = makeClient();
    const dom = {
        modal: null,
        closes: 0,
        restored: 0
    };

    class Node {
        constructor(card = false) {
            this.isCard = card;
            this.length = 1;
            this[0] = this;
            this.attrs = {};
            this.children = [];
        }
        attr(name, value) {
            if (arguments.length === 1) return this.attrs[name];
            this.attrs[name] = value;
            return this;
        }
        addClass() { return this; }
        removeClass() { return this; }
        text() { return this; }
        empty() { return this; }
        on() { return this; }
        first() { return this; }
        append(value) { this.children.push(value); return this; }
        find(selector) {
            if (selector === '.selector') {
                const card = this.children.find(x => x && x.isCard);
                if (card) return card;
            }
            return new Node();
        }
    }

    test.context.$ = function (value) {
        return value instanceof Node ? value : new Node();
    };
    test.client.TranslationSubUi = { posterUrl() { return ''; } };
    test.client.TranslationSubRuntime.restoreController = function () { dom.restored++; };
    test.client.Lampa.Template = {
        get(name) { return new Node(name === 'notice_card'); }
    };
    test.client.Lampa.Modal = {
        open(options) { dom.modal = options; },
        close() { dom.closes++; }
    };
    vm.runInContext(notice, test.context, { filename: 'translationsub-notice.js' });
    assert.equal(typeof test.client.TranslationSubNotice.open, 'function');
    return { ...test, dom };
}

{
    const { client, activities, selections, dom, selectorCloses } = makeNoticeClient();
    client.TranslationSubNotice.open([item([active, activeOther])], true);
    assert.ok(dom.modal.select, 'TV remote gets initial focus on the notification');
    assert.equal(dom.modal.select.attr('data-translationsub-update'), '0');
    dom.modal.onSelect(dom.modal.select); // remote OK on notification

    assert.equal(selections.length, 1, 'open a real Lampa Select menu');
    assert.equal(dom.closes, 0, 'keep notice drawer open behind the selector');
    assert.equal(activities.length, 0);
    assert.equal(selections[0].onBack, undefined,
        'remote Back is handled by the Lampa selector window stack');

    // Lampa itself hides its selector before onSelect runs.
    selections[0].onSelect(selections[0].items[0]);
    assert.equal(selectorCloses(), 0, 'must not manually close an already hidden selector');
    assert.equal(dom.closes, 1, 'close the modal exactly once before navigation');
    assert.equal(activities.length, 2);
    assert.equal(activities[0].component, 'full');
    assert.equal(activities[1].lampac_custom_select, 'collaps');
    console.log('PASS: remote OK opens selection; Back leaves drawer/focus intact');
}

{
    const { client, activities, selections, dom } = makeNoticeClient();
    const events = [];
    const closeModal = client.Lampa.Modal.close;
    const push = client.Lampa.Activity.push;
    client.Lampa.Modal.close = function () {
        events.push('modal closed');
        closeModal();
    };
    client.Lampa.Activity.push = function (activity) {
        events.push('activity opened');
        push(activity);
    };
    client.TranslationSubNotice.open([item([active])], true);
    assert.ok(dom.modal.select, 'single-source notification has an initial remote focus');
    dom.modal.onSelect(dom.modal.select);
    assert.equal(selections.length, 0, 'single source requires no extra remote input');
    assert.equal(dom.closes, 1);
    assert.deepEqual(events, ['modal closed', 'activity opened', 'activity opened'],
        'release the modal controller, then place the card underneath Online');
    assert.equal(activities[0].component, 'full');
    assert.equal(activities[1].component, 'lampac');
    console.log('PASS: remote OK goes directly to Online and hands off focus in order');
}

{
    const { client, activities, selections, dom } = makeNoticeClient();
    client.TranslationSubNotice.open([item([active])], false);
    dom.modal.onSelect(dom.modal.select);
    assert.equal(selections.length, 0);
    assert.equal(dom.closes, 1);
    assert.equal(activities.length, 1, 'disabled mode must not add an extra activity');
    assert.equal(activities[0].component, 'full');
    console.log('PASS: disabled toggle preserves remote OK -> series card');
}

{
    const { client, activities, selections, dom } = makeNoticeClient();
    client.TranslationSubNotice.open([item([stale])], true);
    dom.modal.onSelect(dom.modal.select);
    assert.equal(selections.length, 0);
    assert.equal(activities[0].component, 'full');
    console.log('PASS: missing eligible source falls back to remote-accessible card');
}

new vm.Script(source);
new vm.Script(notice);
console.log('PASS: client JavaScript syntax');
