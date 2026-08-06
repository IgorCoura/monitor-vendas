import { describe, expect, it } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ProxiesPage } from './ProxiesPage'
import { renderMobile, renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer } from '../../test/msw'

function overview(overrides: Record<string, unknown> = {}) {
  return {
    enabled: true,
    activeProxies: 1,
    assignedNumbers: 2,
    numbersWithoutProxy: 0,
    bansInPeriod: 1,
    proxies: [
      {
        id: 'p1',
        shortId: 'abc',
        label: 'IPv4 São Paulo',
        kind: 'Ipv4',
        host: '191.0.0.1',
        port: 8080,
        status: 'Active',
        numbersCount: 2,
        capacity: 2,
        sellersCount: 2,
        bansCount: 1,
        bannedNumbersCount: 1,
        expiresAt: '2026-09-01T00:00:00Z',
        lastTestedAt: '2026-08-03T12:00:00Z',
        lastTestOk: true,
        numbers: [
          { numberId: 'n1', phone: '5511968608425', sellerName: 'Ana', status: 'Active' },
          { numberId: 'n2', phone: '5511911112222', sellerName: 'Bruno', status: 'Active' },
        ],
      },
    ],
    unassigned: [],
    ...overrides,
  }
}

describe('ProxiesPage', () => {
  // A tabela mostra ocupação, vendedores distintos e bans do período de cada proxy.
  it('lista os proxies com ocupação e bans', async () => {
    mswServer.use(http.get('/api/v1/proxies', () => HttpResponse.json(overview())))

    renderWithProviders(<ProxiesPage />)

    expect(await screen.findByText('IPv4 São Paulo')).toBeInTheDocument()
    const table = screen.getByTestId('proxies-table')
    expect(table).toHaveTextContent('2/2')
    expect(table).toHaveTextContent('Ativo')
  })

  // Clicar no nome do proxy revela quais números estão nele, com o vendedor.
  it('mostra os números de um proxy ao expandir', async () => {
    mswServer.use(http.get('/api/v1/proxies', () => HttpResponse.json(overview())))

    renderWithProviders(<ProxiesPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'IPv4 São Paulo' }))

    expect(await screen.findByText('+55 11 96860-8425')).toBeInTheDocument()
    expect(screen.getByText(/Ana/)).toBeInTheDocument()
  })

  // Números sem proxy viram aviso com os telefones: é o sinal de que falta
  // capacidade contratada.
  it('avisa quando há números sem proxy', async () => {
    mswServer.use(
      http.get('/api/v1/proxies', () =>
        HttpResponse.json(
          overview({
            numbersWithoutProxy: 1,
            unassigned: [{ numberId: 'n3', phone: '5511933334444', sellerName: 'Carla', status: 'Active' }],
          }),
        ),
      ),
    )

    renderWithProviders(<ProxiesPage />)

    expect(await screen.findByTestId('without-proxy')).toHaveTextContent('+55 11 93333-4444')
  })

  // Desligado o uso de proxies, a tela explica que as sessões conectadas não são
  // mexidas — tirar todas de uma vez reiniciaria todos os sockets juntos.
  it('explica o efeito de desligar os proxies', async () => {
    mswServer.use(http.get('/api/v1/proxies', () => HttpResponse.json(overview({ enabled: false }))))

    renderWithProviders(<ProxiesPage />)

    expect(await screen.findByText(/continuam nos seus proxies até reconectarem/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ligar proxies' })).toBeInTheDocument()
  })

  // A redistribuição mostra a PRÉVIA antes de aplicar, dizendo quantos números
  // conectados vão reiniciar a sessão.
  it('mostra a prévia da redistribuição antes de aplicar', async () => {
    let applied = false
    mswServer.use(
      http.get('/api/v1/proxies', () => HttpResponse.json(overview())),
      http.get('/api/v1/proxies/allocation/preview', () =>
        HttpResponse.json({
          moves: [
            {
              numberId: 'n1',
              phone: '5511968608425',
              sellerName: 'Ana',
              fromLabel: 'IPv4 São Paulo',
              toLabel: 'IPv4 Rio',
              restartsSocket: true,
            },
          ],
          stillWithoutProxy: [],
        }),
      ),
      http.post('/api/v1/proxies/allocation/apply', () => {
        applied = true
        return HttpResponse.json({ moved: 1, withoutProxy: 0 })
      }),
    )

    renderWithProviders(<ProxiesPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Redistribuir' }))

    const preview = await screen.findByTestId('allocation-preview')
    expect(preview).toHaveTextContent('1 número muda de proxy')
    expect(preview).toHaveTextContent('reinicia a sessão')

    await user.click(screen.getByRole('button', { name: 'Aplicar' }))
    await waitFor(() => expect(applied).toBe(true))
  })

  // Testar o proxy fala com o servidor e o botão mostra o círculo de progresso.
  it('testa o proxy pelo botão da linha', async () => {
    let tested = false
    mswServer.use(
      http.get('/api/v1/proxies', () => HttpResponse.json(overview())),
      http.post('/api/v1/proxies/p1/test', () => {
        tested = true
        return HttpResponse.json({ tested: true })
      }),
    )

    renderWithProviders(<ProxiesPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Testar' }))

    await waitFor(() => expect(tested).toBe(true))
  })

  describe('no celular', () => {
    // No celular a tabela vira cards: as mesmas informações, sem rolagem lateral.
    it('mostra cards no lugar da tabela', async () => {
      mswServer.use(http.get('/api/v1/proxies', () => HttpResponse.json(overview())))

      renderMobile(<ProxiesPage />)

      expect(await screen.findByTestId('proxies-cards')).toBeInTheDocument()
      expect(screen.queryByTestId('proxies-table')).not.toBeInTheDocument()
    })
  })
})
