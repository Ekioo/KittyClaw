// Run only against an isolated instance with mocked providers.
import assert from 'node:assert/strict';
import { register } from '../hooks/register.js';

const base = process.env.KITTYCLAW_TEST_URL;
if (!/^http:\/\/localhost:\d+$/.test(base || '') || /:(5230|5231)$/.test(base)) {
  throw new Error('Set KITTYCLAW_TEST_URL to an isolated localhost test instance (never 5230/5231).');
}
async function api(path, body) {
  const response = await fetch(base + path, body ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {});
  assert.ok(response.ok, path + ': HTTP ' + response.status + ' ' + await response.clone().text());
  return response.json();
}
const project = await api('/api/projects', { name: 'Claude mod smoke ' + Date.now() });
const columns = await api('/api/projects/' + project.slug + '/columns');
assert.ok(columns.length);
const ticket = await api('/api/projects/' + project.slug + '/tickets', {
  title: 'Verify the Claude mod journey', description: 'Synthetic integration fixture.',
  createdBy: 'owner', status: columns[0].name,
});
const hooks = [];
register((name, matcher, handler) => hooks.push({ name, matcher: typeof matcher === 'function' ? null : matcher, handler: handler || matcher }));
const saved = new Map();
let prompt = '';
const elements = Object.fromEntries(['Box', 'Text', 'Button', 'Input', 'Markdown'].map(type => [type, props => ({ type, props })]));
const $ = {
  session: { id: async () => 'integration-session', messages: async () => [{ role: 'assistant', content: [{ type: 'text', text: 'Synthetic result: the mod journey passed.' }] }] },
  store: { get: async key => saved.get(key), set: async (key, value) => saved.set(key, structuredClone(value)) },
  clock: { now: async () => Date.now() }, command: { register: async () => {} },
  prompt: { fill: async ({ text }) => { prompt = text; return { isFilled: true }; } },
  ui: { invalidate() {}, open: async () => {}, resolve: () => elements },
  http: { fetch: async (url, init) => {
    const response = await fetch(url, init);
    return { ok: response.ok, status: response.status, text: await response.text() };
  } },
};
async function fire(name, event) {
  const hook = hooks.find(h => h.name === name && (!h.matcher || Object.entries(h.matcher).every(([k, v]) => event[k] === v)));
  return hook.handler($, event, async () => ({}));
}
async function control(key) {
  const tree = await fire('ui.render', { component: 'Pane', requestId: 'kittyclaw-tracker' });
  function find(node) {
    if (node.props?.key === key) return node.props;
    for (const child of node.props?.children || []) { const found = typeof child === 'object' && find(child); if (found) return found; }
  }
  const found = find(tree);
  assert.ok(found, 'Missing control: ' + key);
  return found;
}
await fire('session.start', { cwd: '/synthetic-workspace' });
await (await control('server')).onSubmit(base);
await fire('command.run', { command: 'kittyclaw' });
await (await control('project-' + project.slug)).onPress();
await (await control('ticket-' + ticket.id)).onPress();
await (await control('work')).onPress();
assert.match(prompt, /Synthetic integration fixture/);
await (await control('copy-result')).onPress();
await (await control('publish')).onPress();
const updated = await api('/api/projects/' + project.slug + '/tickets/' + ticket.id);
assert.equal(updated.comments.length, 1);
assert.match(updated.comments[0].content, /Synthetic result: the mod journey passed/);
assert.equal(updated.comments[0].author, 'claude-code');
assert.equal(updated.status, ticket.status);
console.log('PASS: isolated API choose → work prompt → publish → read back, without moving the ticket.');
