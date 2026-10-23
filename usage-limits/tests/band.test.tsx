import type { On } from 'claude-code'
import { expect, mock, test } from 'claude-code/testing'

const NOW = Date.parse('2026-10-23T12:00:00Z')

const BAND = {
  plugin: 'usage-limits',
  component: 'AbovePrompt',
  props: {
    hasSurvey: false,
    isWorking: false,
    maxRows: 6,
    bodyColumns: 80,
    scroll: { offset: 0, bodyRows: 6 },
    view: {},
  },
} as const

const LIMITS = [
  { kind: 'five_hour', percentUsed: 42, resetsAt: '2026-10-23T14:13:00Z' },
  { kind: 'seven_day', percentUsed: 91, resetsAt: '2026-10-26T12:00:00Z' },
]

function engine(on: On, rateLimits: typeof LIMITS) {
  mock.clock(on, { now: NOW })
  on('command.register', () => ({ value: { command: 'limits' } }))
  on('session.usage', () => ({ value: { startedAt: NOW, context: {} as never, rateLimits } }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
}

test('the band shows both windows with countdowns, on every surface', async ($, on) => {
  engine(on, LIMITS)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  for (const surface of ['terminal', 'desktop'] as const) {
    const ui = await $.ui.mount({ ...BAND, surface })
    expect((await ui.find({ type: 'Text', text: /resets in 2h 13m/ }))?.text).toBe('· resets in 2h 13m')
    expect((await ui.find({ type: 'Text', text: /91%/ }))?.text).toBe('   91%')
    await ui.unmount()
  }
})

test('compact mode shows one line of percentages', async ($, on) => {
  engine(on, LIMITS)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  await ui.press({ key: 'compact' })
  expect((await ui.find({ type: 'Text', text: /^5h/ }))?.text).toBe('5h 42%')
  await ui.unmount()
})

test('no band before any limit data arrives', async ($, on) => {
  engine(on, [])
  on('ui.render', ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text>engine</Text>
  })
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect((await ui.find({ type: 'Text' }))?.text).toBe('engine')
  await ui.unmount()
})
