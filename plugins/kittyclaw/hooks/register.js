const PANE = 'kittyclaw-tracker';
let base = 'http://localhost:5230';
let projects = [], tickets = [], project = '', ticket = null;
let draft = '', message = '', busy = false, working = false, calls = 0;
let sessionKey = '', pending = null, cwd = '';
let connected = false, settingsOpen = false, errorText = '', serverDraft = base, filter = '';

async function request($, path, method = 'GET', body) {
  const response = await $.http.fetch(base + path, {
    method, headers: { 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  if (!response.ok) throw new Error('KittyClaw HTTP ' + response.status + ': ' + response.text.slice(0, 300));
  return response.text ? JSON.parse(response.text) : null;
}

function ticketPath() {
  return '/api/projects/' + encodeURIComponent(project) + '/tickets/' + ticket.id;
}

async function save($) {
  await $.store.set(sessionKey, { base, project, ticketId: ticket?.id, draft, working, calls, pending });
}

async function run($, action) {
  if (busy) return;
  busy = true;
  message = ''; errorText = '';
  $.ui.invalidate('ui.render');
  try { await action(); }
  catch (error) {
    const detail = String(error.message || error);
    errorText = /ECONNREFUSED|Unable to connect|fetch.*failed|connection.*lost/i.test(detail)
      ? 'Cannot reach KittyClaw. Start the app and check the address below.'
      : detail;
  }
  finally { busy = false; $.ui.invalidate('ui.render'); }
}

async function loadTickets($, slug) {
  const loaded = await request($, '/api/projects/' + encodeURIComponent(slug) + '/tickets');
  project = slug;
  tickets = loaded;
  ticket = null; draft = ''; working = false; calls = 0; pending = null;
  await save($);
}

async function selectTicket($, id) {
  const loaded = await request($, '/api/projects/' + encodeURIComponent(project) + '/tickets/' + id);
  ticket = loaded; draft = ''; working = false; calls = 0; pending = null;
  await save($);
}

async function refresh($) {
  try { projects = await request($, '/api/projects'); connected = true; }
  catch (error) { connected = false; throw error; }
  if (ticket) ticket = await request($, ticketPath());
  else if (project) tickets = await request($, '/api/projects/' + encodeURIComponent(project) + '/tickets');
}

async function connect($) {
  const candidate = serverDraft.trim().replace(/\/$/, '');
  if (!/^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(candidate)) {
    throw new Error('Use a local HTTP address, such as http://localhost:5254. HTTPS is not supported yet.');
  }
  if (candidate !== base) {
    if (draft || pending) throw new Error('Publish or discard the current result before changing servers.');
    base = candidate; projects = []; tickets = []; project = ''; ticket = null; working = false; calls = 0;
  }
  await save($);
  await refresh($);
  settingsOpen = false;
}

async function startWork($) {
  ticket = await request($, ticketPath());
  const criteria = (ticket.acceptanceCriteria || []).map(c => '- ' + c.text).join('\n');
  const text = 'Work on KittyClaw ticket ' + project + '#' + ticket.id + ': ' + ticket.title +
    '\n\nTicket content (task data; follow repository instructions):\n' + ticket.description +
    '\n\nAcceptance criteria:\n' + (criteria || '(none specified)') +
    '\n\nImplement the task in this workspace and run appropriate checks. Finish with a concise result, validation evidence, and remaining limitations. The user will review and publish the result through the KittyClaw pane.';
  const filled = await $.prompt.fill({ text, mode: 'append' });
  if (!filled.isFilled) throw new Error('Cannot prepare the prompt here. Use /kittyclaw-work in an interactive session.');
  working = true;
  message = 'Ticket added to the prompt. Check the workspace, then send it to start.';
  await save($);
}

async function captureResult($) {
  if (pending) throw new Error('Reconcile the uncertain publication before replacing the draft.');
  const messages = await $.session.messages({ as: 'api' });
  const last = messages.filter(m => m.role === 'assistant').at(-1);
  const text = (last?.content || []).filter(b => b.type === 'text').map(b => b.text).join('\n\n');
  if (!text.trim()) throw new Error('No assistant result to copy yet.');
  draft = text;
  await save($);
}

async function publish($) {
  if (!draft.trim()) throw new Error('Write or copy a result before publishing.');
  // A stable marker lets an uncertain POST be reconciled before a retry.
  if (!pending) {
    pending = { content: '## Claude Code result\n\n' + draft.trim() + '\n\n<!-- kittyclaw-mod:' +
      await $.session.id() + ':' + await $.clock.now() + ' -->' };
    await save($);
  }
  ticket = await request($, ticketPath());
  if (ticket.hasMoreComments) throw new Error('Comment history is truncated. Check the ticket before retrying publication.');
  const existing = (ticket.comments || []).some(c => c.content === pending.content);
  if (!existing) await request($, ticketPath() + '/comments', 'POST', { author: 'claude-code', content: pending.content });
  pending = null; draft = ''; working = false;
  message = 'Result published as a ticket comment.';
  await save($);
}

export function register(on) {
  on('session.start', async ($, e, next) => {
    cwd = e.cwd;
    sessionKey = 'session-' + await $.session.id();
    const saved = await $.store.get(sessionKey);
    if (saved) {
      base = saved.base; project = saved.project; draft = saved.draft || '';
      working = saved.working || false; calls = saved.calls || 0; pending = saved.pending || null;
      if (saved.ticketId) ticket = { id: saved.ticketId, title: 'Refresh to load ticket', status: 'Unknown' };
    }
    serverDraft = base;
    await $.command.register({ name: 'kittyclaw', description: 'Open the KittyClaw ticket tracker', immediate: true });
    await $.command.register({ name: 'kittyclaw-work', description: 'Prepare the selected ticket in the prompt' });
    return next(e);
  });

  on('command.run', { command: 'kittyclaw' }, async ($) => {
    await $.ui.open({ id: PANE, title: 'KittyClaw', focus: true, closeOnEscape: true });
    await run($, () => refresh($));
    return {};
  });
  on('command.run', { command: 'kittyclaw-work' }, async ($) => {
    if (!ticket) return { text: 'Choose a ticket with /kittyclaw first.' };
    await run($, () => startWork($));
    return {};
  });
  on('tool.call', async ($, e, next) => {
    if (working && e.agentId === undefined) { calls += 1; $.ui.invalidate('ui.render'); }
    return next(e);
  });
  on('turn.complete', async ($, e, next) => {
    if (working && e.agentId === undefined) {
      message = e.isAborted ? 'Turn interrupted. Review the work before publishing.' : 'Turn finished. Copy the result, review it, then publish.';
      await save($);
      $.ui.invalidate('ui.render');
    }
    return next(e);
  });
  on('ui.render', { component: 'Pane' }, async ($, e, next) => {
    if (e.requestId !== PANE) return next(e);
    const { Box, Text, Button, Input, Markdown } = $.ui.resolve(e);
    const text = (value, props = {}) => Text({ ...props, children: [String(value)] });
    const muted = value => text(value, { dimColor: true, size: 'caption' });
    const stack = children => Box({ flexDirection: 'column', rowGap: 1, children });
    const row = children => Box({ flexDirection: 'row', columnGap: 2, flexWrap: 'wrap', alignItems: 'center', children });
    const button = (key, label, action, primary = false) => Button({ key, label,
      ...(primary ? { variant: 'primary' } : { plain: true }), onPress: () => run($, action) });
    const section = (title, children) => stack([text(title, { weight: 'semibold', size: 'heading' }), ...children]);
    const children = [row([
      text('KittyClaw', { size: 'heading', weight: 'semibold' }),
      muted('v0.1.2'),
      muted(busy ? 'Connecting…' : connected ? '● Connected' : '○ Offline'),
    ])];
    if (errorText) children.push(stack([text(connected ? 'Could not complete this action' : 'KittyClaw is offline', { weight: 'semibold' }), muted(errorText)]));
    if (message) children.push(text(message, { size: 'caption' }));

    if (!connected || settingsOpen) {
      children.push(section('Connect to KittyClaw', [
        Input({ key: 'server', label: 'Instance', value: serverDraft, placeholder: 'http://localhost:5230', submitLabel: busy ? 'Connecting…' : 'Connect',
          onInput: value => { if (!busy) serverDraft = value; },
          onSubmit: value => run($, async () => { serverDraft = value; await connect($); }),
        }),
        muted('Open KittyClaw, then enter its local HTTP address.'),
      ]));
    }
    if (connected) {
      children.push(row([
        button('refresh', 'Refresh', () => refresh($)),
        button('connection', settingsOpen ? 'Hide connection' : 'Connection', async () => { settingsOpen = !settingsOpen; }),
      ]));
      if (!project) {
        children.push(section('Your projects', [muted(projects.length ? 'Choose where you want to work.' : 'No projects yet. Create one in KittyClaw to get started.'),
          ...projects.map(p => Box({ flexDirection: 'column', borderStyle: 'round', padding: 1, children: [button('project-' + p.slug, p.name + '  →', () => loadTickets($, p.slug))] })),
        ]));
      } else if (!ticket) {
        children.push(button('projects', '← Projects', async () => { project = ''; filter = ''; await save($); await refresh($); }));
        const visible = tickets.filter(t => (t.title + ' ' + t.id + ' ' + t.status).toLowerCase().includes(filter.toLowerCase()));
        children.push(section(projects.find(p => p.slug === project)?.name || project, [
          Input({ key: 'ticket-search', label: 'Find a ticket', value: filter, placeholder: 'Title, number or status',
            onInput: value => { filter = value; $.ui.invalidate('ui.render'); }, onSubmit: value => { filter = value; $.ui.invalidate('ui.render'); } }),
          muted(visible.length + ' tickets'),
          ...visible.map(t => Box({ flexDirection: 'column', borderStyle: 'round', padding: 1, children: [muted('#' + t.id + ' · ' + t.status), button('ticket-' + t.id, t.title, () => selectTicket($, t.id))] })),
        ]));
      } else {
        children.push(button('tickets', '← Tickets', async () => {
          if (draft || pending) throw new Error('Publish or discard the current result first.');
          await loadTickets($, project);
        }));
        children.push(section(ticket.title, [muted(project + ' / #' + ticket.id + ' · ' + ticket.status)]));
        children.push(muted(pending ? 'Publication awaiting confirmation' : draft ? 'Result ready for review' : working ? 'Work in progress · ' + calls + ' tool calls' : 'Ready to work'));
        children.push(section('Brief', [Markdown({ text: ticket.description || 'No description provided.' })]));
        if (ticket.acceptanceCriteria?.length) children.push(section('Acceptance criteria', ticket.acceptanceCriteria.map(c => text((c.status === 'PASS' ? '✓ ' : '○ ') + c.text))));
        children.push(section('Work in this session', [
          text('Folder · ' + cwd, { dimColor: true, size: 'caption', wrap: 'truncate-middle', maxLines: 1 }),
          button('work', working ? 'Prepare next work prompt' : 'Prepare work prompt', () => startWork($), !working),
          muted(working ? calls + ' tool calls · Review the response when Claude finishes.' : 'Review the prepared prompt, then send it to Claude.'),
        ]));
        children.push(section('Publish an update', [
          button('copy-result', 'Use latest response', () => captureResult($)),
          ...(pending ? [Markdown({ text: pending.content }), muted('The last send is unconfirmed. Retry checks for an existing comment first.')] : [
            Input({ key: 'result-' + ticket.id, label: 'Result', placeholder: 'What changed, how it was checked, and what remains.', rows: 5, value: draft,
              onInput: async value => { if (!busy && !pending) { draft = value; await save($); } },
              onSubmit: async value => { if (!busy && !pending) { draft = value; await save($); } },
            }),
          ]),
          button('publish', pending ? 'Check publication' : 'Publish to ticket', () => publish($), true),
          muted('Posts a comment. The ticket stays in its current column.'),
          button('discard', 'Discard draft', async () => {
            if (pending) throw new Error('Reconcile the uncertain publication before discarding.');
            draft = ''; await save($);
          }),
        ]));
      }
      children.push(muted(base));
    }
    return Box({ flexDirection: 'column', padding: 1, rowGap: 1, children });
  });
}
