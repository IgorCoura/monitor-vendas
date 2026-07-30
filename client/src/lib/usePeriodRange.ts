import { useCallback, useEffect, useMemo, useState } from 'react'
import { rangeForPeriod, type Period } from './format'
import { usePersistedState } from './usePersistedState'
import type { PollMs } from './polling'

const PERIOD_KEY = 'mv:period'
const validPeriods: Period[] = ['hoje', 7, 30, 90]

function sanitizePeriod(value: unknown): Period {
  return validPeriods.includes(value as Period) ? (value as Period) : 30
}

function truncateToMinute(date: Date): Date {
  const copy = new Date(date)
  copy.setSeconds(0, 0)
  return copy
}

// Período escolhido (persistido, compartilhado entre dashboard e página do
// vendedor) + intervalo "vivo": o fim da janela avança junto com a atualização
// automática, e `refreshNow` reposiciona na hora (atualização manual).
//
// Sem isso o `to` ficaria congelado no instante em que a página abriu — como ele
// entra na queryKey, a atualização automática buscaria sempre a MESMA janela e
// mensagens novas nunca apareceriam sem refresh do navegador.
export function usePeriodRange(pollMs: PollMs) {
  const [period, setPeriod] = usePersistedState<Period>(PERIOD_KEY, 30, sanitizePeriod)
  const [now, setNow] = useState(() => truncateToMinute(new Date()))

  useEffect(() => {
    if (pollMs === null) return
    const id = setInterval(() => setNow(truncateToMinute(new Date())), pollMs)
    return () => clearInterval(id)
  }, [pollMs])

  const refreshNow = useCallback(() => setNow(truncateToMinute(new Date())), [])
  const range = useMemo(() => rangeForPeriod(period, now), [period, now])

  return { period, setPeriod, range, refreshNow }
}
