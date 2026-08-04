import { describe, expect, it } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { WarmupPage } from './WarmupPage'
import { renderMobile, renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer } from '../../test/msw'

function overview(overrides: Record<string, unknown> = {}) {
  return {
    enabled: true,
    haltedAt: null,
    haltReason: null,
    peersInPool: 2,
    messagesToday: 12,
    conversationsToday: 3,
    deliveryRate: 0.92,
    numbers: [
      {
        peerId: 'w1',
        numberId: 'n1',
        phone: '5511968608425',
        sellerName: 'Ana',
        numberStatus: 'Active',
        inPool: true,
        ineligibleReason: null,
        persona: 'Seco',
        coreCircle: 1,
        occasionalCircle: 0,
        circle: ['5511911112222'],
        goal: 32,
        effectiveGoal: 6,
        cappedByGraph: true,
        realMessagesToday: 4,
        warmupMessagesToday: 2,
      },
      {
        peerId: null,
        numberId: 'n2',
        phone: '5511911112222',
        sellerName: 'Bruno',
        numberStatus: 'BannedTemporary',
        inPool: false,
        ineligibleReason: 'em cooldown pós-ban',
        persona: null,
        coreCircle: 0,
        occasionalCircle: 0,
        circle: [],
        goal: 0,
        effectiveGoal: 0,
        cappedByGraph: false,
        realMessagesToday: 0,
        warmupMessagesToday: 0,
      },
    ],
    conversations: [
      {
        id: 'c1',
        theme: 'combinar o almoço',
        status: 'Completed',
        phoneA: '5511968608425',
        phoneB: '5511911112222',
        createdAt: '2026-08-04T12:00:00Z',
        completedAt: '2026-08-04T12:30:00Z',
        archived: true,
        turns: [
          {
            sequence: 1,
            fromPhone: '5511968608425',
            text: 'bora almoçar?',
            scheduledAt: '2026-08-04T12:01:00Z',
            sentAt: '2026-08-04T12:01:00Z',
            delivered: true,
          },
          {
            sequence: 2,
            fromPhone: '5511911112222',
            text: 'bora, meio dia',
            scheduledAt: '2026-08-04T12:05:00Z',
            sentAt: null,
            delivered: false,
          },
        ],
      },
    ],
    ...overrides,
  }
}

function stub(overrides: Record<string, unknown> = {}) {
  mswServer.use(http.get('/api/v1/warmup', () => HttpResponse.json(overview(overrides))))
}

describe('WarmupPage', () => {
  // Os KPIs do topo resumem o pool: quantos números, quanto saiu hoje e se as
  // mensagens estão chegando.
  it('mostra o resumo do pool', async () => {
    stub()

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText('Números no pool')).toBeInTheDocument()
    expect(screen.getByText('12')).toBeInTheDocument()
    expect(screen.getByText('92%')).toBeInTheDocument()
  })

  // A tabela mostra quanto cada número já mandou hoje contra a meta, e quanto
  // veio de cliente de verdade.
  it('lista os números com a meta do dia', async () => {
    stub()

    renderWithProviders(<WarmupPage />)

    const table = await screen.findByTestId('warmup-table')
    expect(table).toHaveTextContent('2 de 6')
    expect(table).toHaveTextContent('4 com clientes')
  })

  // Meta capada pelo tamanho do pool é dita na tela: sem isso "6" pareceria a
  // meta configurada, e o operador não saberia que faltam números.
  it('avisa quando a meta foi capada pela capacidade do grafo', async () => {
    stub()

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText(/meta 32 capada pelo pool/)).toBeInTheDocument()
  })

  // Número que não pode participar mostra o motivo: "fora do pool" sem
  // explicação vira chamado de suporte.
  it('explica por que um número não participa', async () => {
    stub()

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText('em cooldown pós-ban')).toBeInTheDocument()
  })

  // Clicar no telefone revela com quem aquele número conversa.
  it('mostra o círculo de um número ao expandir', async () => {
    stub()

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: '+55 11 96860-8425' }))

    expect(await screen.findByText(/Conversa com \+55 11 91111-2222/)).toBeInTheDocument()
  })

  // Monitorar o aquecimento sem poder ler o que ele mandou seria confiar no
  // escuro: a conversa abre com o texto inteiro.
  it('abre a conversa com o texto das mensagens', async () => {
    stub()

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByText('combinar o almoço'))

    const dialog = await screen.findByTestId('warmup-conversation')
    expect(dialog).toHaveTextContent('bora almoçar?')
    expect(dialog).toHaveTextContent('bora, meio dia')
    // Mensagem que ainda não saiu aparece com a hora prevista, não como enviada.
    expect(dialog).toHaveTextContent(/sai /)
  })

  // Colocar um número no aquecimento é um POST; a lista é recarregada depois.
  it('coloca um número no pool', async () => {
    stub()
    let posted: unknown = null
    mswServer.use(
      http.post('/api/v1/warmup/peers', async ({ request }) => {
        posted = await request.json()
        return new HttpResponse(null, { status: 200 })
      }),
    )

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Colocar' }))

    await waitFor(() => expect(posted).toEqual({ numberId: 'n2' }))
  })

  // Tirar do pool é um DELETE no número, não no peer: o histórico fica.
  it('tira um número do pool', async () => {
    stub()
    let deleted: string | null = null
    mswServer.use(
      http.delete('/api/v1/warmup/peers/:numberId', ({ params }) => {
        deleted = String(params.numberId)
        return new HttpResponse(null, { status: 204 })
      }),
    )

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Tirar' }))

    await waitFor(() => expect(deleted).toBe('n1'))
  })

  // O botão de pânico pergunta antes: parar o pool inteiro não é clique de
  // passagem.
  it('pede confirmação antes de parar tudo', async () => {
    stub()
    let halted = false
    mswServer.use(
      http.post('/api/v1/warmup/halt', () => {
        halted = true
        return new HttpResponse(null, { status: 204 })
      }),
    )

    renderWithProviders(<WarmupPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Parar tudo agora' }))

    expect(halted).toBe(false)
    await user.click(screen.getByRole('button', { name: 'Parar tudo' }))
    await waitFor(() => expect(halted).toBe(true))
  })

  // Pool parado pelo kill switch mostra o motivo e diz que religar é manual.
  it('mostra o motivo quando o kill switch disparou', async () => {
    stub({
      haltedAt: '2026-08-04T13:00:00Z',
      haltReason: 'Taxa de entrega do pool em 40%, abaixo do mínimo.',
    })

    renderWithProviders(<WarmupPage />)

    const banner = await screen.findByTestId('warmup-halted')
    expect(banner).toHaveTextContent('Taxa de entrega do pool em 40%')
    expect(banner).toHaveTextContent(/Religar é decisão manual/)
    // E não dá para parar de novo o que já está parado.
    expect(screen.getByRole('button', { name: 'Parar tudo agora' })).toBeDisabled()
  })

  // Desligado, a tela diz em texto que nada é gerado nem enviado.
  it('avisa quando o aquecimento está desligado', async () => {
    stub({ enabled: false })

    renderWithProviders(<WarmupPage />)

    expect(await screen.findByText(/nenhuma mensagem entre os números é gerada/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ligar aquecimento' })).toBeInTheDocument()
  })

  // No celular a tabela vira cards, com o círculo no "ver mais".
  it('vira cards no celular', async () => {
    stub()

    renderMobile(<WarmupPage />)

    expect(await screen.findByTestId('warmup-cards')).toBeInTheDocument()
    expect(screen.queryByTestId('warmup-table')).not.toBeInTheDocument()
  })
})
