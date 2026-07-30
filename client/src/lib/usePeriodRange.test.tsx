import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import { usePeriodRange } from './usePeriodRange'

function Probe({ pollMs = 60_000 }: { pollMs?: number | null }) {
  const { period, range } = usePeriodRange(pollMs)
  return (
    <>
      <span data-testid="period">{String(period)}</span>
      <span data-testid="to">{range.to}</span>
    </>
  )
}

describe('usePeriodRange', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-30T12:00:30Z'))
  })

  afterEach(() => vi.useRealTimers())

  // O fim da janela avança com o tempo: sem isso o polling buscaria sempre o
  // intervalo congelado na abertura da página e dados novos só apareceriam com refresh.
  it('avança o fim do intervalo conforme o tempo passa', () => {
    render(<Probe />)
    const first = screen.getByTestId('to').textContent
    // Segundos truncados: a janela é estável dentro do mesmo minuto.
    expect(first).toBe('2026-07-30T12:00:00.000Z')

    // advanceTimersByTime move o clock junto: 12:00:30 + 60s = 12:01:30 → trunca 12:01.
    act(() => vi.advanceTimersByTime(60_000))

    expect(screen.getByTestId('to').textContent).toBe('2026-07-30T12:01:00.000Z')
  })

  // Com a atualização automática desligada a janela fica congelada — o usuário
  // decide quando buscar (botão de atualizar).
  it('não avança a janela quando o polling está desligado', () => {
    render(<Probe pollMs={null} />)
    const first = screen.getByTestId('to').textContent

    act(() => vi.advanceTimersByTime(10 * 60_000))

    expect(screen.getByTestId('to').textContent).toBe(first)
  })

  // Período inválido no localStorage (versão antiga) cai no default de 30 dias.
  it('sanitiza período inválido vindo do storage', () => {
    localStorage.setItem('mv:period', JSON.stringify('semana-passada'))

    render(<Probe />)

    expect(screen.getByTestId('period').textContent).toBe('30')
  })
})
