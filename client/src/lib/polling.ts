import { usePersistedState } from './usePersistedState'

// Intervalo de atualização automática. `null` = desligado (só atualização manual).
export type PollMs = number | null

export const pollOptions: { value: PollMs; label: string; help: string }[] = [
  { value: 60_000, label: '1min', help: 'Atualizar a cada 1 minuto' },
  { value: 300_000, label: '5min', help: 'Atualizar a cada 5 minutos' },
  { value: 600_000, label: '10min', help: 'Atualizar a cada 10 minutos' },
  { value: null, label: 'Off', help: 'Desligar atualização automática' },
]

const POLL_KEY = 'mv:pollMs'

function sanitizePollMs(value: unknown): PollMs {
  return pollOptions.some((o) => o.value === value) ? (value as PollMs) : 60_000
}

export function usePollMs() {
  return usePersistedState<PollMs>(POLL_KEY, 60_000, sanitizePollMs)
}
