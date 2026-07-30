import { describe, expect, it } from 'vitest'
import { sanitizeChartKeys } from './metrics'

describe('sanitizeChartKeys', () => {
  // Chaves válidas salvas pelo usuário são mantidas na ordem em que estavam.
  it('mantém chaves válidas', () => {
    expect(sanitizeChartKeys(['sales', 'conversion'])).toEqual(['sales', 'conversion'])
  })

  // Chave de métrica que não existe mais (versão antiga do app) é descartada.
  it('descarta chaves desconhecidas', () => {
    expect(sanitizeChartKeys(['conversion', 'metrica-que-nao-existe'])).toEqual(['conversion'])
  })

  // Duplicata é removida — a métrica é exclusiva entre gráficos.
  it('remove duplicatas', () => {
    expect(sanitizeChartKeys(['sales', 'sales'])).toEqual(['sales'])
  })

  // Lixo no storage (null, string, lista vazia) cai no default de um gráfico.
  it('cai no default quando o valor é inválido', () => {
    expect(sanitizeChartKeys(null)).toEqual(['conversion'])
    expect(sanitizeChartKeys('conversion')).toEqual(['conversion'])
    expect(sanitizeChartKeys([])).toEqual(['conversion'])
  })
})
