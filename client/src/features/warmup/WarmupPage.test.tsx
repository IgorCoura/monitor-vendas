import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { WarmupPage } from './WarmupPage'
import { renderMobile, renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer } from '../../test/msw'

function warmupNumber(overrides: Record<string, unknown> = {}) {
  return {
    numberId: 'n1',
    phone: '5511968608425',
    sellerName: 'Ana',
    numberStatus: 'Active',
    state: 'Warming',
    day: 5,
    totalDays: 30,
    messagesPerDay: 50,
    messagesToday: 18,
    newContactsPerDay: 2,
    newContactsToday: 1,
    startedAt: '2026-07-30T12:00:00Z',
    pausedAt: null,
    completedAt: null,
    atCeiling: false,
    ...overrides,
  }
}

function overview(numbers: Record<string, unknown>[], extra: Record<string, unknown> = {}) {
  return {
    enabled: true,
    warming: numbers.length,
    mature: 0,
    atCeiling: numbers.filter((n) => n.atCeiling).length,
    curve: [
      { throughDay: 3, messagesPerDay: 20, newContactsPerDay: 0 },
      { throughDay: 7, messagesPerDay: 50, newContactsPerDay: 2 },
    ],
    numbers,
    ...extra,
  }
}

describe('WarmupPage', () => {
  // A linha mostra em que dia da curva o número está e quanto do teto já usou —
  // é o dado que hoje não existe em lugar nenhum.
  it('mostra o dia da curva e o consumo do dia', async () => {
    mswServer.use(http.get('/api/v1/warmup', () => HttpResponse.json(overview([warmupNumber()]))))

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText('+55 11 96860-8425')).toBeInTheDocument()
    const table = screen.getByTestId('warmup-table')
    expect(table).toHaveTextContent('Dia 5 de 30')
    expect(table).toHaveTextContent('18/50')
    expect(table).toHaveTextContent('Em aquecimento')
  })

  // Quem bateu o teto é destacado: é a resposta para "por que este número parou
  // de enviar?".
  it('destaca quem atingiu o teto do dia', async () => {
    mswServer.use(
      http.get('/api/v1/warmup', () =>
        HttpResponse.json(overview([warmupNumber({ messagesToday: 50, atCeiling: true })])),
      ),
    )

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText('Teto do dia atingido')).toBeInTheDocument()
  })

  // Número aquecido não mostra dia nem teto: a curva não se aplica mais a ele.
  it('mostra o número aquecido sem teto', async () => {
    mswServer.use(
      http.get('/api/v1/warmup', () =>
        HttpResponse.json(
          overview([warmupNumber({ state: 'Mature', messagesPerDay: null, messagesToday: 120 })]),
        ),
      ),
    )

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText('Aquecido')).toBeInTheDocument()
    expect(screen.getByTestId('warmup-table')).toHaveTextContent('120 (sem teto)')
  })

  // A curva configurada fica visível sob demanda, para o teto não parecer
  // arbitrário.
  it('mostra a curva configurada', async () => {
    mswServer.use(http.get('/api/v1/warmup', () => HttpResponse.json(overview([warmupNumber()]))))

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Ver a curva' }))

    expect(await screen.findByTestId('warmup-curve')).toHaveTextContent('até 20 mensagens/dia')
  })

  // Reiniciar a curva devolve o número ao dia 1.
  it('reinicia a curva pelo menu', async () => {
    let restarted = false
    mswServer.use(
      http.get('/api/v1/warmup', () => HttpResponse.json(overview([warmupNumber()]))),
      http.post('/api/v1/numbers/n1/warmup/restart', () => {
        restarted = true
        return HttpResponse.json({})
      }),
    )

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Ações de aquecimento/ }))
    await user.click(await screen.findByRole('menuitem', { name: /Reiniciar curva/ }))

    await waitFor(() => expect(restarted).toBe(true))
  })

  // Marcar como aquecido afrouxa a proteção: pede confirmação antes.
  it('pede confirmação para marcar como aquecido', async () => {
    let completed = false
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    mswServer.use(
      http.get('/api/v1/warmup', () => HttpResponse.json(overview([warmupNumber()]))),
      http.post('/api/v1/numbers/n1/warmup/complete', () => {
        completed = true
        return HttpResponse.json({})
      }),
    )

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Ações de aquecimento/ }))
    await user.click(await screen.findByRole('menuitem', { name: 'Marcar como aquecido' }))

    expect(confirmSpy).toHaveBeenCalled()
    expect(completed).toBe(false)
    confirmSpy.mockRestore()
  })

  describe('no celular', () => {
    // No celular a tabela vira cards, com os mesmos dados.
    it('mostra cards no lugar da tabela', async () => {
      mswServer.use(http.get('/api/v1/warmup', () => HttpResponse.json(overview([warmupNumber()]))))

      renderMobile(<WarmupPage />)

      const cards = await screen.findByTestId('warmup-cards')
      expect(within(cards).getByText('+55 11 96860-8425')).toBeInTheDocument()
      expect(screen.queryByTestId('warmup-table')).not.toBeInTheDocument()
    })
  })
})
