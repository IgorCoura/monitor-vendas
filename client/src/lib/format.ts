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

// <input type="date"> trabalha em "YYYY-MM-DD" no fuso local; a API espera ISO
// UTC. O dia escolhido vai inteiro: da meia-noite local ao último instante dele.
export function toDayInput(date: Date): string {
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

export function dayStartIso(day: string): string {
  return new Date(`${day}T00:00:00`).toISOString()
}

export function dayEndIso(day: string): string {
  return new Date(`${day}T23:59:59.999`).toISOString()
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

// Telefone na forma que o usuário lê: +55 11 91234-4567. O banco guarda só
// dígitos com DDI; a máscara é só aqui, para nenhuma tela inventar a sua.
// O que não couber no formato brasileiro sai como veio — inventar formato para
// número estrangeiro seria pior que não formatar.
export function fmtPhone(value: string | null | undefined): string {
  const digits = (value ?? '').replace(/\D/g, '')
  if (digits.length === 0) return '—'

  const national = digits.startsWith('55') && (digits.length === 12 || digits.length === 13)
    ? digits.slice(2)
    : digits

  if (national.length !== 10 && national.length !== 11) return digits

  const area = national.slice(0, 2)
  const subscriber = national.slice(2)
  const cut = subscriber.length === 9 ? 5 : 4

  return `+55 ${area} ${subscriber.slice(0, cut)}-${subscriber.slice(cut)}`
}
