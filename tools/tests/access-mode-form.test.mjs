import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

// Exercise the shipped script without provisioning LAN listeners or public tunnels.
const source = readFileSync(new URL('../../KittyClaw.Web/Services/AccessSetupView.cs', import.meta.url), 'utf8');
const script = source.match(/public const string Script = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
function browser(currentMode) {
    let mode = currentMode;
    const handlers = {};
    const posts = [];
    const consent = { checked: currentMode === 'internet', focus() { this.focused = true; } };
    const form = {
        dataset: { currentMode },
        querySelector: selector => selector.includes(':checked') ? { value: mode } : consent,
        addEventListener: (name, handler) => handlers[name] = handler,
        setAttribute(name, value) { this[name] = value; },
    };
    vm.runInNewContext(script, {
        document: { querySelectorAll: () => [form] },
        HTMLFormElement: { prototype: { submit() {
            posts.push({ mode, consent: consent.checked, inert: this.inert });
        } } },
    });
    return {
        posts, form, consent,
        choose(value) { mode = value; handlers.change({ target: { name: 'mode' } }); },
        allow() { consent.checked = true; handlers.change({ target: consent }); },
        enter() { handlers.submit({ preventDefault() {} }); },
    };
}

test('LAN and local choices submit immediately, once, and keep the controls enabled', () => {
    for (const [from, to] of [['local', 'network'], ['network', 'local'], ['internet', 'local']]) {
        const b = browser(from);
        b.choose(to); b.choose(to); b.enter();
        assert.equal(b.posts.length, 1);
        assert.equal(b.posts[0].mode, to);
        assert.equal(b.posts[0].inert, true);
        assert.equal(b.form.disabled, undefined);
        assert.equal(b.form['aria-busy'], 'true');
    }
});
test('Internet waits for explicit consent, including keyboard submission', () => {
    const b = browser('local');
    b.choose('internet'); b.enter();
    assert.equal(b.posts.length, 0);
    assert.equal(b.consent.focused, true);
    b.allow(); b.allow(); b.enter();
    assert.equal(b.posts.length, 1);
    assert.deepEqual(b.posts[0], { mode: 'internet', consent: true, inert: true });
});
test('unchanged modes and cancelled Internet selection never submit', () => {
    const b = browser('local');
    b.choose('local'); b.enter(); b.choose('internet'); b.choose('local'); b.enter();
    assert.equal(b.posts.length, 0);
});
