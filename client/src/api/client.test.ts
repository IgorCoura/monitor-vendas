import { describe, expect, it } from 'vitest'
import { resolveApiBase } from './client'

describe('resolveApiBase', () => {
  // Regressão: o Dockerfile define VITE_API_BASE_URL como string vazia quando o
  // build não recebe o build-arg. Com `??`, o vazio passava e o bundle chamava
  // `/reports/ranking` — o nginx só encaminha `/api`, então a tela recebia o
  // index.html com status 200 no lugar do JSON.
  it('trata string vazia como ausência e cai no default relativo', () => {
    expect(resolveApiBase('')).toBe('/api/v1')
    expect(resolveApiBase('   ')).toBe('/api/v1')
  })

  // Sem a variável definida, o front chama a própria origem e o nginx encaminha.
  it('usa o default quando a variável não existe', () => {
    expect(resolveApiBase(undefined)).toBe('/api/v1')
  })

  // Só serve quando o navegador precisa falar com outro domínio; aí o valor vale.
  it('respeita a base configurada quando ela tem conteúdo', () => {
    expect(resolveApiBase('https://api.exemplo.com/api/v1')).toBe('https://api.exemplo.com/api/v1')
  })
})
