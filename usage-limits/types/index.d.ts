export type LimitWindow = { kind: string; percentUsed: number; resetsAt?: string }

export type DisplayMode = 'full' | 'compact' | 'hidden'

export type RenderDiag = {
  calls: number
  drawn: number
  surface: string
  hasSurvey: boolean
  lastAt: number
}

declare module 'claude-code' {
  interface PluginState {
    'usage-limits': {
      windows: Shaped<LimitWindow[]>
      mode: DisplayMode
      warned: Record<string, number>
    }
  }
}
