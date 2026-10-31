#!/usr/bin/env node
'use strict';
// Exercises the actual TranslationSub runtime helper in a deterministic Lampa
// simulator; no real browser is started in this test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const code = fs.readFileSync(path.resolve(__dirname, '../../translationsub.js'), 'utf8');
function harness() {
    const pending = [];
    const calls = [];
    const full = { component: 'full' };
    const online = { component: 'lampac' };
    const subscriptions = { component: 'translationsub_list' };
    let active = full;
    let controller = 'select';
    const client = { appready: true };
    const Lampa = {
        Controller: {
            enabled() { return { name: controller }; },
            toggle(name) { calls.push(name); controller = name; }
        },
        Activity: {
            active() { return active; },
            push(activity) { active = activity; }
        }
    };
    client.Lampa = Lampa;
    vm.runInNewContext(code, {
        window: client, Lampa, console: { log() {} },
        setTimeout(callback) { pending.push(callback); },
        setInterval() { throw new Error('runtime should already be ready'); },
        clearInterval() {}
    }, { filename: 'translationsub.js' });
    assert.ok(client.TranslationSubRuntime, 'runtime exposed');
    function flush() {
        while (pending.length) pending.shift()();
    }
    return {
        client, calls, full, online, subscriptions, flush,
        switchTo(activity, name) { active = activity; controller = name; },
        controller() { return controller; }
    };
}
{
    const h = harness();
    h.client.TranslationSubRuntime.restoreController('full_start', 'content');
    h.switchTo(h.online, 'content');
    h.flush();
    assert.deepEqual(h.calls, [],
        'queued restoration from a previous card must not steal Online focus');
    assert.equal(h.controller(), 'content');
    console.log('PASS: previous full-card focus is not restored over Online');
}
{
    const h = harness();
    h.client.TranslationSubRuntime.restoreController('full_descr', 'content');
    h.switchTo(h.subscriptions, 'content');
    h.flush();
    assert.deepEqual(h.calls, [],
        'queued restoration must not steal subscription-page focus');
    console.log('PASS: previous card description does not steal subscriptions focus');
}
{
    const h = harness();
    h.client.TranslationSubRuntime.restoreController('full_start', 'content');
    h.flush();
    assert.deepEqual(h.calls, ['full_start'], 'same-activity menu Back must still restore card');
    console.log('PASS: same-card controller is restored after closing a chooser');
}
{
    const h = harness();
    h.client.TranslationSubRuntime.restoreController('content');
    h.switchTo(h.online, 'content');
    h.flush();
    assert.deepEqual(h.calls, [], 'pending content re-toggle cannot override a new activity');
    console.log('PASS: queued content restoration is scoped to the originating activity');
}
{
    const h = harness();
    h.switchTo(h.subscriptions, 'select');
    h.client.TranslationSubRuntime.restoreController('content');
    h.switchTo(h.online, 'content');
    h.flush();
    assert.deepEqual(h.calls, [],
        'a pending subscription-menu restoration cannot disrupt the next Online activity');
    console.log('PASS: leaving subscriptions for Online cancels stale controller restoration');
}
{
    const h = harness();
    h.switchTo(h.subscriptions, 'modal');
    h.client.TranslationSubRuntime.restoreController('content');
    h.flush();
    assert.deepEqual(h.calls, ['content'], 'same-activity subscription actions can restore focus');
    console.log('PASS: same-activity subscription action can restore content focus');
}
