#!/usr/bin/env node
'use strict';

// Run the real TranslationSub subscription page in a small simulated Lampa
// host, verifying repeated activity transitions. Not a browser/TV test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const pageSource = fs.readFileSync(path.resolve(__dirname, '../../translationsub-page.js'), 'utf8');

function makeHost() {
    const tasks = [];
    const activities = [];
    const classes = source => String(source || '').match(/class="([^"]*)"/);
    class Node {
        constructor(source) {
            this.source = String(source || '');
            this.classNames = (classes(this.source) || ['', ''])[1].split(/\s+/);
            this.children = [];
            this.length = 1;
            this[0] = this;
            this.focused = false;
            this.attrs = {};
            this.offsetParent = {};
            const attrs = this.source.match(/data-subscription-id="([^"]*)"/);
            if (attrs) this.attrs['data-subscription-id'] = attrs[1];
        }
        append(child) { this.children.push(typeof child === 'string' ? new Node(child) : child); return this; }
        empty() { this.children = []; return this; }
        find(selector) {
            const selectors = selector.split(',').map(s => s.trim());
            const found = [];
            function scan(parent) {
                parent.children.forEach(node => {
                    if (!(node instanceof Node)) return;
                    if (selectors.some(rule => {
                        const names = rule.match(/\.[\w-]+/g) || [];
                        return names.every(c => c === '.focus' ? node.focused : node.classNames.includes(c.slice(1)));
                    })) found.push(node);
                    scan(node);
                });
            }
            scan(this);
            return new Selection(found);
        }
        on() { return this; }
        attr(name, value) {
            if (arguments.length === 1) return this.attrs[name] || '';
            this.attrs[name] = String(value);
            return this;
        }
        remove() { return this; }
    }
    class Selection {
        constructor(items) { this.items = items; this.length = items.length; if (items.length) this[0] = items[0]; }
        first() { return new Selection(this.items.slice(0, 1)); }
        attr(name) { return this.items[0] ? this.items[0].attr(name) : ''; }
        each(fn) { this.items.forEach((item, i) => fn.call(item, i, item)); return this; }
    }
    function $(item) {
        if (item instanceof Node) return item;
        return new Node(item);
    }
    let activeController = '';
    const controllers = {};
    const controller = {
        add(name, value) { controllers[name] = value; },
        enabled() { return { name: activeController }; },
        toggle(name) {
            activeController = name;
            if (controllers[name] && controllers[name].toggle) controllers[name].toggle();
        },
        enable(name) { if (activeController === name) this.toggle(name); },
        collectionSet(node) {
            this.collection = node;
            if (this.focused) this.focused.focused = false;
            this.focused = null;
        },
        collectionFocus(target) {
            assert.ok(target, 'must select an actual subscription element');
            this.focused = target;
            target.focused = true;
        },
        collection: null,
        focused: null
    };

    const snapshot = {
        subscriptions: [{ id: 'sub-1', title: 'Series', translationName: 'Voice' }],
        badge: { count: 1 }
    };
    const client = {
        TranslationSubRuntime: { onReady(callback) { callback(); },
            controllerName() { return activeController; } },
        TranslationSubBadgeState: {
            subscribe(callback) { callback({ snapshot }); return () => {}; },
            refresh(callback) { callback(); }
        }
    };
    const Lampa = {
        Controller: controller,
        Component: {
            add(name, factory) { this[name] = factory; }
        },
        Activity: {
            active() { return activities[activities.length - 1] || null; },
            backward() { activities.pop(); }
        },
        Scroll: class {
            constructor() { this.root = new Node('<div class="scroll"></div>'); }
            minus() {}
            append(node) { this.root.append(node); }
            render() { return this.root; }
            update() {}
            destroy() {}
        },
        Utils: { escape(value) { return value; } },
        Noty: { show() {} }
    };
    const Navigator = {
        canmove() { return false; },
        move() {}
    };
    client.Lampa = Lampa;
    const context = vm.createContext({
        window: client, Lampa, Navigator, $, setTimeout(fn) { tasks.push(fn); },
        console
    });
    vm.runInContext(pageSource, context, { filename: 'translationsub-page.js' });
    assert.equal(typeof Lampa.Component.translationsub_list, 'function');

    function openFull() {
        const activity = { component: 'full', activity: {} };
        activities.push(activity);
        controller.add('full_start', { toggle() {} });
        controller.toggle('full_start');
        return activity;
    }
    function openOnline() {
        const activity = { component: 'lampac', activity: {} };
        activities.push(activity);
        controller.add('content', {
            toggle() {
                controller.collectionSet(new Node('<div class="online"></div>'));
                controller.collectionFocus(new Node('<div class="selector online-prestige--full"></div>'));
            }
        });
        controller.toggle('content');
        return activity;
    }
    function openSubscriptions() {
        const page = new Lampa.Component.translationsub_list();
        const slide = { loader() {}, toggle() { if (this.is_started) page.start(); },
            is_started: true };
        const activity = { component: 'translationsub_list', activity: slide };
        page.activity = slide;
        page.create();
        activities.push(activity);
        page.start();
        return activity;
    }
    function flush() {
        for (let n = 0; tasks.length && n < 40; n++) tasks.shift()();
        assert.equal(tasks.length, 0, 'all deterministic navigation timers completed');
    }
    function state() {
        return { screen: Lampa.Activity.active().component,
            controller: activeController, focused: !!controller.focused };
    }
    return { openFull, openOnline, openSubscriptions, flush, state };
}

{
    const host = makeHost();
    for (let i = 0; i < 5; i++) {
        host.openFull();
        host.openOnline();
        host.openSubscriptions();
        host.flush();
        assert.deepEqual(host.state(), {
            screen: 'translationsub_list', controller: 'content', focused: true
        }, 'subscription focus must work after Online and after repeated transitions');
    }
    console.log('PASS: 5 repeated Full → Online → subscriptions transitions, without external button plugin');
}

console.log('PASS: repeated subscription navigation checks');
