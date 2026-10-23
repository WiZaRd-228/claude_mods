import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, SessionRateLimit } from 'claude-code'

import type { DisplayMode, LimitWindow, RenderDiag } from '../types'

const COMMAND = 'limits'
const PANE = 'usage-limits'
const TICK_MS = 60_000
const WARN_THRESHOLDS = [80, 95]
const WARN_LEVEL = 70
const DANGER_LEVEL = 90

const windows = atom({ plugin: 'usage-limits', key: 'windows' } as const, [], {
  shape: 'windows-1',
})
const mode = atom({ plugin: 'usage-limits', key: 'mode' } as const, 'full' as DisplayMode)
const warned = atom({ plugin: 'usage-limits', key: 'warned' } as const, {} as Record<string, number>)
let diag: RenderDiag | null = null

const LABELS: Record<string, string> = {
  five_hour: '5h',
  seven_day: '7d',
  spend_limit: 'spend',
}

const MODE_ARGS: Record<string, DisplayMode> = {
  full: 'full',
  show: 'full',
  compact: 'compact',
  hide: 'hidden',
}

export function labelOf(kind: string) {
  return LABELS[kind] ?? kind
}

export function colorOf(percent: number) {
  return percent >= DANGER_LEVEL ? 'error' : percent >= WARN_LEVEL ? 'warning' : 'success'
}

export function isReset(w: LimitWindow, now: number) {
  return w.resetsAt !== undefined && Date.parse(w.resetsAt) <= now
}

// A window past its reset time has no fresh reading until the next reply, so show it as empty.
export function current(w: LimitWindow, now: number): LimitWindow {
  return isReset(w, now) ? { kind: w.kind, percentUsed: 0 } : w
}

export function formatLeft(resetsAt: string | undefined, now: number) {
  if (!resetsAt) return ''
  const minutes = Math.max(0, Math.ceil((Date.parse(resetsAt) - now) / 60_000))
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  const mins = minutes % 60
  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${mins}m`
  return `${mins}m`
}

export function bar(percent: number, width: number) {
  const filled = Math.min(width, Math.round((Math.min(percent, 100) / 100) * width))
  return '█'.repeat(filled) + '░'.repeat(width - filled)
}

function summarize(w: LimitWindow, now: number) {
  const left = formatLeft(w.resetsAt, now)
  return `${labelOf(w.kind)}: ${w.percentUsed}% used${left ? `, resets in ${left}` : ''}`
}

function compactLine(list: LimitWindow[], now: number) {
  return list.map(w => `${labelOf(w.kind)} ${current(w, now).percentUsed}%`).join(' · ')
}

function describeDiag(d: RenderDiag | null, now: number) {
  if (!d) return 'Band: this client has not asked the mod to draw above the prompt yet.'
  const ago = Math.round((now - d.lastAt) / 1000)
  return `Band: asked ${d.calls}x, drawn ${d.drawn}x, surface ${d.surface}, survey ${d.hasSurvey}, last ${ago}s ago.`
}

async function refreshViews($: EngineInterface) {
  const now = await $.clock.now()
  const list = await read($, windows)
  const shown = await read($, mode)
  $.ui.status(list.length === 0 || shown === 'hidden' ? undefined : `Claude ${compactLine(list, now)}`)
  $.ui.invalidate('ui.render')
}

async function store($: EngineInterface, limits: readonly SessionRateLimit[]) {
  if (limits.length === 0) return
  const list: LimitWindow[] = limits.map(({ kind, percentUsed, resetsAt }) => ({
    kind,
    percentUsed,
    resetsAt,
  }))
  await update($, windows, () => list)

  const now = await $.clock.now()
  const seen = await read($, warned)
  const nextWarned: Record<string, number> = {}
  for (const w of list) {
    const id = `${w.kind}:${w.resetsAt ?? ''}`
    const crossed = WARN_THRESHOLDS.filter(t => w.percentUsed >= t).pop() ?? 0
    const before = seen[id] ?? 0
    nextWarned[id] = Math.max(before, crossed)
    if (crossed > before) $.ui.toast(`Claude ${summarize(w, now)}`, { timeoutMs: 8000 })
  }
  await update($, warned, () => nextWarned)
  await refreshViews($)
}

async function setMode($: EngineInterface, value: DisplayMode) {
  await update($, mode, () => value)
  await refreshViews($)
}

function noteRender(surface: string, hasSurvey: boolean, didDraw: boolean, now: number) {
  diag = {
    calls: (diag?.calls ?? 0) + 1,
    drawn: (diag?.drawn ?? 0) + (didDraw ? 1 : 0),
    surface,
    hasSurvey,
    lastAt: now,
  }
}

export const register: Register = on => {
  let stopTick: (() => void) | undefined

  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: COMMAND,
      description: 'Show Claude usage limits; /limits full | compact | hide | pane',
    })
    stopTick?.()
    const timer = $.clock.every(TICK_MS, () => void refreshViews($))
    stopTick = () => timer.cancel()
    await store($, (await $.session.usage()).rateLimits)

    return next(e)
  })

  on('session.measure', async ($, e, next) => {
    if (e.changed.includes('rateLimits')) await store($, e.rateLimits)

    return next(e)
  })

  on('command.run', { command: COMMAND }, async ($, e) => {
    try {
      const arg = e.args.trim().toLowerCase()
      if (arg === 'pane') {
        const { isPlaced } = await $.ui.open({ id: PANE, title: 'Claude limits' })
        return { text: isPlaced ? 'Limits pane opened.' : 'This client cannot show a side pane.' }
      }
      const chosen = MODE_ARGS[arg]
      if (chosen) {
        await setMode($, chosen)
        return { text: `Limits band: ${chosen}` }
      }

      await store($, (await $.session.usage()).rateLimits)
      const list = await read($, windows)
      const now = await $.clock.now()
      const report = describeDiag(diag, now)
      if (list.length === 0) {
        return { text: `No limit data yet: it arrives with the first model reply on a Claude subscription.\n${report}` }
      }

      return { text: `${list.map(w => summarize(current(w, now), now)).join('\n')}\n${report}` }
    } catch {
      return { text: 'Could not read usage limits right now.' }
    }
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const list = await read($, windows)
    const shown = await read($, mode)
    const now = await $.clock.now()
    const willDraw = !e.props.hasSurvey && list.length > 0 && shown !== 'hidden'
    noteRender(e.surface, e.props.hasSurvey, willDraw, now)
    if (!willDraw) return next(e)

    const { Box, Text, Button } = $.ui.resolve(e)

    if (shown === 'compact') {
      return (
        <Box flexDirection="row" gap={2}>
          {list.map(raw => {
            const w = current(raw, now)
            return (
              <Text mono color={colorOf(w.percentUsed)}>
                {labelOf(w.kind)} {w.percentUsed}%
              </Text>
            )
          })}
          <Button key="expand" label="Expand" onPress={() => setMode($, 'full')} />
        </Box>
      )
    }

    const barWidth = Math.max(6, Math.min(24, Math.floor(e.props.bodyColumns / 4)))

    return (
      <Box flexDirection="column">
        {list.map(raw => {
          const w = current(raw, now)
          const left = formatLeft(w.resetsAt, now)
          return (
            <Box flexDirection="row" gap={1}>
              <Text mono dimColor>
                {labelOf(w.kind).padEnd(5)}
              </Text>
              <Text mono color={colorOf(w.percentUsed)}>
                {bar(w.percentUsed, barWidth)}
              </Text>
              <Text mono color={colorOf(w.percentUsed)}>
                {`${w.percentUsed}%`.padStart(6)}
              </Text>
              {left !== '' && <Text dimColor>· resets in {left}</Text>}
            </Box>
          )
        })}
        <Box flexDirection="row" gap={1}>
          <Button key="compact" label="Compact" onPress={() => setMode($, 'compact')} />
          <Button key="hide" label="Hide" onPress={() => setMode($, 'hidden')} />
        </Box>
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Text } = $.ui.resolve(e)
    const list = await read($, windows)
    const now = await $.clock.now()
    if (list.length === 0) return <Text dimColor>No limit data yet.</Text>

    const barWidth = Math.max(6, Math.min(30, e.props.bodyColumns - 12))

    return (
      <Box flexDirection="column" gap={1}>
        {list.map(raw => {
          const w = current(raw, now)
          const left = formatLeft(w.resetsAt, now)
          return (
            <Box flexDirection="column">
              <Text weight="semibold">
                {labelOf(w.kind)} · {w.percentUsed}%
              </Text>
              <Text mono color={colorOf(w.percentUsed)}>
                {bar(w.percentUsed, barWidth)}
              </Text>
              {left !== '' && <Text dimColor>resets in {left}</Text>}
            </Box>
          )
        })}
      </Box>
    )
  })
}
