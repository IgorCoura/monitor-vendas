import { describe, expect, it } from 'vitest'
import { fmtDate, fmtHours, fmtMinutes, fmtPercent, fmtUptime, periodRange } from './format'

describe('formatadores', () => {
  // Percentual: fração 0–1 vira "NN%"; null vira travessão.
  it('fmtPercent formata fração e trata null', () => {
    expect(fmtPercent(0.5)).toBe('50%')
    expect(fmtPercent(1)).toBe('100%')
    expect(fmtPercent(null)).toBe('—')
  })

  // Regressão (04/08/2026): 99,53% saía como "100%" por causa do toFixed(0), e um
  // número banido no dia anterior passava por canal perfeito. "100%" só quando é
  // 100 mesmo; null (sem número a medir) vira travessão.
  it('fmtUptime nunca arredonda para cima nem inventa 100%', () => {
    expect(fmtUptime(100)).toBe('100%')
    expect(fmtUptime(99.53)).toBe('99,5%')
    expect(fmtUptime(99.99)).toBe('99,9%')
    expect(fmtUptime(0)).toBe('0%')
    expect(fmtUptime(null)).toBe('—')
  })

  // Minutos: abaixo de 1h mostra "N min"; acima vira "Xh MM" com zero à esquerda.
  it('fmtMinutes converte para horas quando passa de 60', () => {
    expect(fmtMinutes(45)).toBe('45 min')
    expect(fmtMinutes(125)).toBe('2h 05')
    expect(fmtMinutes(null)).toBe('—')
  })

  // Horas com uma casa decimal; null vira travessão.
  it('fmtHours formata com uma casa', () => {
    expect(fmtHours(5)).toBe('5.0h')
    expect(fmtHours(null)).toBe('—')
  })

  // Data ISO vira dd/mm/aaaa sem depender de timezone do browser.
  it('fmtDate formata ISO como pt-BR', () => {
    expect(fmtDate('2026-09-07')).toBe('07/09/2026')
    expect(fmtDate('2026-09-07T00:00:00Z')).toBe('07/09/2026')
  })

  // periodRange devolve o intervalo de N dias terminando em "agora".
  it('periodRange calcula from N dias antes de to', () => {
    const now = new Date('2026-07-30T12:00:00Z')
    const range = periodRange(30, now)
    expect(range.to).toBe('2026-07-30T12:00:00.000Z')
    expect(range.from).toBe('2026-06-30T12:00:00.000Z')
  })
})
