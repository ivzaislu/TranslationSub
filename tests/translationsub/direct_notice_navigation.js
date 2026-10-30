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
            push(value) { activities.push(value); }
        },
        Select: {
            show(options) { selections.push(options); },
            close() {}
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
    return { client, storage, activities, selections, context };
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
    assert.equal(activities.length, 1);
    assert.equal(selections.length, 0);
    assert.equal(activities[0].component, 'lampac');
    assert.equal(activities[0].lampac_custom_select, 'collaps');
    assert.equal(activities[0].movie.id, 123);
    assert.equal(activities[0].movie.name, 'Example');
    assert.equal(storage.online_choice_collaps[123].season, 1);
    assert.equal(storage.online_choice_collaps[123].voice_name, 'LostFilm');
    assert.equal(storage.online_choice_collaps[123].voice_url, '');
    assert.equal(storage.online_choice_collaps[123].episodes_view[1], 2);
    console.log('PASS: one active balancer -> Lampac activity, season and voice preselected');
}

{
    const { client, activities, selections } = makeClient();
    assert.equal(client.TranslationSubCardSource.openNoticeOnline(item([active, activeOther])), true);
    assert.equal(activities.length, 0, 'must not arbitrarily select between active balancers');
    assert.equal(selections.length, 1);
    assert.equal(selections[0].items.length, 2);
    selections[0].onSelect(selections[0].items[1]);
    assert.equal(activities.length, 1);
    assert.equal(activities[0].lampac_custom_select, 'hdvb');
    console.log('PASS: two balancers -> explicit selection');
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

// Validate that both source and notification scripts can parse in the client VM.
vm.Script; // Kept explicit for easy use in downstream runners.
new vm.Script(source);
new vm.Script(notice);
console.log('PASS: client JavaScript syntax');
