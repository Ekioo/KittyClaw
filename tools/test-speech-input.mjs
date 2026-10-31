import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../KittyClaw.Web/wwwroot/js/speech-input.js', import.meta.url));
const { formatSpeechResults, attach } = await import('data:text/javascript;base64,' + source.toString('base64'));
const phrase = (text, final = true) => ({ text, final });
assert.equal(formatSpeechResults([phrase('bonjour')], 'fr'), 'Bonjour.');
assert.equal(formatSpeechResults([phrase('bonjour'), phrase('tout va bien', false)], 'fr'), 'Bonjour. Tout va bien');
assert.equal(formatSpeechResults([phrase('bonjour', false)], 'fr', true), 'Bonjour.');
assert.equal(formatSpeechResults([phrase('bonjour, tout va bien.')], 'fr'), 'Bonjour, tout va bien.');
assert.equal(formatSpeechResults([phrase('bonjour virgule comment vas-tu point d’interrogation')], 'fr'), 'Bonjour, comment vas-tu\u202f?');
assert.equal(formatSpeechResults([phrase('attention point d’exclamation')], 'fr'), 'Attention\u202f!');
assert.equal(formatSpeechResults([phrase('premier point-virgule deuxième point final')], 'fr'), 'Premier\u202f; deuxième.');
assert.equal(formatSpeechResults([phrase('faire le point')], 'fr'), 'Faire le point.');
assert.equal(formatSpeechResults([phrase('suite', false)], 'fr', false, false), 'suite');
assert.equal(formatSpeechResults([phrase('bonjour'), phrase('virgule comment vas-tu point d’interrogation')], 'fr'), 'Bonjour, comment vas-tu\u202f?');
let latest;
class Recognition { constructor() { latest = this; } start() {} stop() { this.onend?.(); } abort() { this.aborted = true; } }
globalThis.window = { SpeechRecognition: Recognition, isSecureContext: true };
globalThis.document = { body: {}, addEventListener() {}, removeEventListener() {} };
globalThis.MutationObserver = class { observe() {} disconnect() {} };
const target = new EventTarget();
Object.assign(target, { value: '', selectionStart: 0, selectionEnd: 0, maxLength: -1, isConnected: true, focus() {},
    setRangeText(text, start, end) { this.value = this.value.slice(0, start) + text + this.value.slice(end); this.selectionStart = this.selectionEnd = start + text.length; } });
const scope = new EventTarget(); scope.querySelector = () => target;
const toggle = { setAttribute() {}, contains(el) { return el === this; } };
const status = { textContent: '' };
const root = { isConnected: true, dataset: { start: 'Start', stop: 'Stop', language: 'fr' }, closest: () => scope,
    querySelector: selector => selector === '.speech-toggle' ? toggle : status };
const controller = attach(root);
const result = (text, isFinal = false) => latest.onresult({ results: [Object.assign([{ transcript: text }], { isFinal })] });
toggle.onclick(); result('bonjour'); assert.equal(target.value, 'Bonjour');
result('bonjour tout le monde', true); assert.equal(target.value, 'Bonjour tout le monde.');
result('bonjour tout le monde', true); assert.equal(target.value, 'Bonjour tout le monde.');
target.value = 'Texte corrigé !'; target.selectionStart = target.selectionEnd = target.value.length;
target.dispatchEvent(new Event('input')); result('bonjour tout le monde', true);
assert.equal(target.value, 'Texte corrigé !'); toggle.onclick(); assert.equal(target.value, 'Texte corrigé !');
target.value = ''; target.selectionStart = target.selectionEnd = 0;
toggle.onclick(); result('message prêt');
const session = latest, late = latest.onresult;
const click = new Event('click'); Object.defineProperty(click, 'target', { value: { closest: () => ({}) } }); scope.dispatchEvent(click);
assert.equal(target.value, 'Message prêt.'); assert.equal(session.aborted, true);
target.value = ''; late({ results: [[{ transcript: 'late' }]] }); assert.equal(target.value, '');
controller.dispose();
console.log('Speech input checks passed: sentence marks, capitalization, French commands, no duplicate punctuation, editable draft, send finalization and late-result protection.');

function availabilityFixture() {
    const button = { disabled: false, setAttribute() {}, contains() { return false; } };
    const message = { textContent: '' };
    const host = { isConnected: true, dataset: { start: 'Start', stop: 'Stop', unavailable: 'Unsupported browser', insecure: 'HTTPS required', denied: 'Microphone denied', noMicrophone: 'No microphone', serviceDenied: 'Service denied', languageUnsupported: 'Language unsupported', disabled: 'false', language: 'fr' },
        closest: () => scope, querySelector: selector => selector === '.speech-toggle' ? button : message };
    return { host, button, message, controller: attach(host) };
}
window.SpeechRecognition = null;
let fixture = availabilityFixture();
assert.equal(fixture.button.disabled, true); assert.equal(fixture.host.title, 'Unsupported browser');
fixture.controller.refresh(); assert.equal(fixture.button.disabled, true); fixture.controller.dispose();
window.SpeechRecognition = Recognition; window.isSecureContext = false;
fixture = availabilityFixture(); assert.equal(fixture.button.disabled, true); assert.equal(fixture.button.title, 'HTTPS required'); fixture.controller.dispose();
window.isSecureContext = true;
for (const [error, expected] of [['not-allowed', 'Microphone denied'], ['service-not-allowed', 'Service denied'], ['audio-capture', 'No microphone'], ['language-not-supported', 'Language unsupported']]) {
    fixture = availabilityFixture(); fixture.button.onclick(); latest.onerror({ error });
    assert.equal(fixture.button.disabled, true); assert.equal(fixture.host.dataset.reason, expected);
    fixture.controller.refresh(); assert.equal(fixture.button.disabled, true); fixture.controller.dispose();
}
const permission = { state: 'denied' };
window.navigator = { permissions: { query: async () => permission } };
fixture = availabilityFixture(); await Promise.resolve(); await Promise.resolve();
assert.equal(fixture.button.disabled, true); assert.equal(fixture.host.title, 'Microphone denied');
permission.state = 'granted'; permission.onchange(); assert.equal(fixture.button.disabled, false);
fixture.host.dataset.disabled = 'true'; fixture.controller.refresh(); assert.equal(fixture.button.disabled, true);
fixture.host.dataset.disabled = 'false'; fixture.controller.refresh(); assert.equal(fixture.button.disabled, false);
fixture.controller.dispose(); assert.equal(permission.onchange, null);
console.log('Availability checks passed: unsupported browser, insecure origin, denied permission, missing device, blocked service/language, rerender protection and permission recovery.');
