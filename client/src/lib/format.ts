// Formatadores de exibição — todos tolerantes a null (API devolve null quando
// o denominador é zero) e exibindo "—" nesse caso.

export function fmtPercent(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  return `${(value * 100).toFixed(0)}%`
}

export function fmtMinutes(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  const total = Math.round(value)
  if (total < 60) return `${total} min`
  const hours = Math.floor(total / 60)
  const minutes = total % 60
  return `${hours}h ${minutes.toString().padStart(2, '0')}`
}

export function fmtHours(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  return `${value.toFixed(1)}h`
}

export function fmtRatio(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  return value.toFixed(1)
}

export function fmtDate(iso: string): string {
  const [year, month, day] = iso.split('T')[0].split('-')
  return `${day}/${month}/${year}`
}

export function fmtPerHour(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  return `${value.toFixed(1)}/h`
}

export function fmtDateTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('pt-BR', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}

// Intervalo dos últimos N dias em ISO UTC, para os query params from/to.
export function periodRange(days: number, now: Date = new Date()): { from: string; to: string } {
  const to = now.toISOString()
  const from = new Date(now.getTime() - days * 24 * 60 * 60 * 1000).toISOString()
  return { from, to }
}

// "Hoje": da meia-noite local até agora.
export function todayRange(now: Date = new Date()): { from: string; to: string } {
  const start = new Date(now)
  start.setHours(0, 0, 0, 0)
  return { from: start.toISOString(), to: now.toISOString() }
}

export type Period = 'hoje' | 7 | 30 | 90

export const periodOptions: { value: Period; label: string }[] = [
  { value: 'hoje', label: 'Hoje' },
  { value: 7, label: '7 dias' },
  { value: 30, label: '30 dias' },
  { value: 90, label: '90 dias' },
]

export function rangeForPeriod(period: Period, now: Date = new Date()): { from: string; to: string } {
  return period === 'hoje' ? todayRange(now) : periodRange(period, now)
}
