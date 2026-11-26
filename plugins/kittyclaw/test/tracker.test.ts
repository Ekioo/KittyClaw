import { expect, test, mock } from 'claude-code/testing'

const PANE = { plugin: 'kittyclaw', component: 'Pane', requestId: 'kittyclaw-tracker', props: {} }

function setup(on, options = {}) {
  const comments = []
  const writes = []
  const prompts = []
  const reads = []
  let failAfterWrite = options.failAfterWrite
  mock.store(on)
  mock.clock(on)
  on('session.id', () => ({ value: 'test-session' }))
  on('session.start', () => ({ cwd: '/work' }))
  on('command.register', () => ({ value: undefined }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('prompt.fill', ($, e) => { prompts.push(e.text); return { isFilled: true } })
  on('session.messages', () => ({ value: [{ role: 'assistant', content: [{ type: 'text', text: 'Implemented the fix. Tests passed.' }] }] }))
  on('http.fetch', ($, e) => {
    reads.push(e.url)
    if (options.offline) return { deny: 'ECONNREFUSED: Unable to connect. Is the computer able to access the url?' }
    const path = e.url.replace(/^http:\/\/localhost:\d+/, '')
    const ticket = { id: 7, title: 'Fix navigation', description: 'Preserve selection.', status: 'Todo', acceptanceCriteria: [{ text: 'Selection persists', status: 'Pending' }], comments }
    let result
    if (e.init?.method === 'POST') {
      const body = JSON.parse(e.init.body)
      writes.push({ path, body }); comments.push(body)
      if (failAfterWrite) { failAfterWrite = false; return { deny: 'connection lost after write' } }
      result = { id: comments.length, ...body }
    } else if (path === '/api/projects') result = [{ slug: 'demo', name: 'Demo' }]
    else if (path === '/api/projects/demo/tickets') result = [ticket]
    else if (path === '/api/projects/demo/tickets/7') result = ticket
    else return { value: { ok: false, status: 404, headers: {}, text: 'missing' } }
    return { value: { ok: true, status: e.init?.method === 'POST' ? 201 : 200, headers: {}, text: JSON.stringify(result) } }
  })
  return { writes, prompts, reads }
}

test('ticket journey renders on terminal, desktop and VS Code and publishes only on click', async ($, on) => {
  const { writes, prompts } = setup(on)
  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.command.run({ command: 'kittyclaw', args: '' })
  const ui = await $.ui.mount({ ...PANE, surface: 'terminal' })
  await ui.press({ key: 'project-demo' })
  await ui.press({ key: 'ticket-7' })
  await ui.press({ key: 'work' })
  expect(prompts[0]).toContain('Preserve selection.')
  expect(prompts[0]).toContain('Selection persists')
  await ui.press({ key: 'copy-result' })
  expect(writes).toHaveLength(0)
  for (const surface of ['desktop', 'vscode']) {
    const view = await $.ui.mount({ ...PANE, surface })
    expect(await view.find({ key: 'publish' })).toBeDefined()
    await view.unmount()
  }
  await ui.press({ key: 'publish' })
  expect(writes).toHaveLength(1)
  expect(writes[0].body.author).toBe('claude-code')
  expect(writes[0].body.content).toContain('Tests passed.')
  expect(await ui.find({ type: 'Text', text: 'Result published as a ticket comment.' })).toBeDefined()
  await ui.press({ key: 'publish' })
  expect(writes).toHaveLength(1)
})

test('uncertain comment publication reconciles before retrying', async ($, on) => {
  const { writes } = setup(on, { failAfterWrite: true })
  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.command.run({ command: 'kittyclaw', args: '' })
  const ui = await $.ui.mount({ ...PANE, surface: 'terminal' })
  await ui.press({ key: 'project-demo' }); await ui.press({ key: 'ticket-7' })
  await ui.press({ key: 'copy-result' }); await ui.press({ key: 'publish' })
  expect(writes).toHaveLength(1)
  await ui.press({ key: 'publish' })
  expect(writes).toHaveLength(1)
  expect(await ui.find({ type: 'Text', text: 'Result published as a ticket comment.' })).toBeDefined()
})

test('work command requires selection', async ($, on) => {
  setup(on)
  const result = await $.command.run({ command: 'kittyclaw-work', args: '' })
  expect(result.text).toContain('Choose a ticket')
})

test('reviewed multiline draft survives session reload and is the published content', async ($, on) => {
  const { writes } = setup(on)
  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.command.run({ command: 'kittyclaw', args: '' })
  const ui = await $.ui.mount({ ...PANE, surface: 'terminal' })
  await ui.press({ key: 'project-demo' }); await ui.press({ key: 'ticket-7' })
  await ui.input({ key: 'result-7', text: 'Reviewed result\n\nValidation: passed.' })
  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.command.run({ command: 'kittyclaw', args: '' })
  await ui.press({ key: 'publish' })
  expect(writes).toHaveLength(1)
  expect(writes[0].body.content).toContain('Reviewed result\n\nValidation: passed.')
})

test('offline connection has a focused recovery view and Connect uses the edited address', async ($, on) => {
  const options = { offline: true }
  const { reads } = setup(on, options)
  await $.session.start({ surface: 'desktop', isInteractive: true, cwd: '/work' })
  await $.command.run({ command: 'kittyclaw', args: '' })
  const ui = await $.ui.mount({ ...PANE, surface: 'desktop' })
  expect(await ui.find({ type: 'Text', text: 'KittyClaw is offline' })).toBeDefined()
  expect(await ui.find({ key: 'project-demo' })).toBeUndefined()
  expect(await ui.find({ key: 'connect' })).toBeUndefined()
  await ui.input({ key: 'server', text: 'https://localhost:5254' })
  expect(await ui.find({ type: 'Text', text: /Use a local HTTP address/ })).toBeDefined()
  expect(reads).toHaveLength(1)
  options.offline = false
  await ui.input({ key: 'server', text: 'http://localhost:5254' })
  expect(reads.at(-1)).toBe('http://localhost:5254/api/projects')
  expect(await ui.find({ key: 'server' })).toBeUndefined()
  expect(await ui.find({ key: 'project-demo' })).toBeDefined()
})

test('ticket search narrows the selection by title, number and status', async ($, on) => {
  setup(on)
  await $.session.start({ surface: 'desktop', isInteractive: true, cwd: '/work' })
  await $.command.run({ command: 'kittyclaw', args: '' })
  const ui = await $.ui.mount({ ...PANE, surface: 'desktop' })
  await ui.press({ key: 'project-demo' })
  await ui.input({ key: 'ticket-search', text: 'missing', kind: 'change' })
  expect(await ui.find({ key: 'ticket-7' })).toBeUndefined()
  await ui.input({ key: 'ticket-search', text: 'navigation', kind: 'change' })
  expect(await ui.find({ key: 'ticket-7' })).toBeDefined()
})
