import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AiAnalysisPage } from './AiAnalysisPage'
import { renderMobile, renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer, seller } from '../../test/msw'
import type { AiAnalysisRowDto } from '../../api/types'

function row(overrides: Partial<AiAnalysisRowDto> = {}): AiAnalysisRowDto {
  return {
    conversationId: crypto.randomUUID(),
    analysisId: crypto.randomUUID(),
    sellerId: 's1',
    sellerName: 'Ana',
    contactName: 'Maria',
    contactPhone: '5511977776666',
    startedAt: '2026-07-20T12:00:00Z',
    lastMessageAt: '2026-07-21T12:00:00Z',
    realOutcome: null,
    aiStatus: 'Clientes perdidos',
    aiStatusCode: 'lost',
    confidence: 0.9,
    divergent: true,
    evidence: 'achei caro',
    lossReason: 'Preço',
    askedForSale: false,
    ignoredBuyingSignal: true,
    objections: 'preço alto',
    shouldRecontact: true,
    recontactReason: 'sumiu depois do orçamento',
    suggestedMessage: 'consigo melhorar a condição',
    interest: 'kit',
    summary: 'cliente achou caro e sumiu',
    conductAlert: null,
    model: 'gemini-3.6-flash',
    analyzedAt: '2026-07-22T12:00:00Z',
    versions: 2,
    ...overrides,
  }
}

function analysesHandler(onRequest?: (url: URL) => void, items: AiAnalysisRowDto[] = [row()]) {
  return http.get('/api/v1/ai/analyses', ({ request }) => {
    onRequest?.(new URL(request.url))
    return HttpResponse.json({ items, page: 1, pageSize: 50, total: items.length })
  })
}

describe('AiAnalysisPage', () => {
  // A lista mostra a leitura da IA ao lado da etiqueta real e marca a divergência.
  it('lista as análises já feitas', async () => {
    mswServer.use(analysesHandler())

    renderWithProviders(<AiAnalysisPage />)

    const table = await screen.findByTestId('analyses-table')
    expect(within(table).getByText('Maria')).toBeInTheDocument()
    expect(within(table).getByText('Clientes perdidos')).toBeInTheDocument()
    expect(within(table).getByText('Preço')).toBeInTheDocument()
    expect(within(table).getByText('Sim')).toBeInTheDocument()
  })

  // Clicar na linha abre o detalhe com evidência e mensagem sugerida — o que não
  // cabe na tabela mas é o que torna a análise acionável.
  it('abre o detalhe da linha', async () => {
    mswServer.use(analysesHandler())

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await user.click(within(await screen.findByTestId('analyses-table')).getByText('Maria'))

    expect(await screen.findByText(/achei caro/)).toBeInTheDocument()
    expect(screen.getByText(/consigo melhorar a condição/)).toBeInTheDocument()
    expect(screen.getByText(/2 versões/)).toBeInTheDocument()
  })

  // Os filtros da tela vão para a API — é assim que o usuário escolhe o recorte.
  it('envia os filtros escolhidos', async () => {
    const ana = seller('Ana')
    let lastUrl: URL | null = null
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      analysesHandler((url) => {
        lastUrl = url
      }),
    )

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await screen.findByTestId('analyses-table')

    await user.selectOptions(screen.getByLabelText('Vendedor'), ana.id)
    await user.selectOptions(screen.getByLabelText('Motivo da perda'), 'preco')
    await user.selectOptions(screen.getByLabelText('Divergência'), 'true')

    await waitFor(() => {
      expect(lastUrl!.searchParams.get('sellerId')).toBe(ana.id)
      expect(lastUrl!.searchParams.get('lossReason')).toBe('preco')
      expect(lastUrl!.searchParams.get('divergent')).toBe('true')
    })
  })

  // O botão de analisar dispara o job com o filtro atual e a tela acompanha.
  it('dispara a análise das conversas do filtro', async () => {
    let posted: { sellerIds: string[]; from: string } | null = null
    mswServer.use(
      analysesHandler(),
      http.post('/api/v1/ai/analyses/run', async ({ request }) => {
        posted = (await request.json()) as { sellerIds: string[]; from: string }
        return HttpResponse.json(
          { id: 'job-1', kind: 'Analyze', status: 'Pending', total: 2, processed: 0, skipped: 0, costBrl: 0, error: null, createdAt: '', completedAt: null },
          { status: 202 },
        )
      }),
      http.get('/api/v1/ai/jobs/job-1', () =>
        HttpResponse.json({ id: 'job-1', kind: 'Analyze', status: 'Completed', total: 2, processed: 2, skipped: 0, costBrl: 0.12, error: null, createdAt: '', completedAt: '' }),
      ),
    )

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await screen.findByTestId('analyses-table')

    await user.click(screen.getByRole('button', { name: 'Analisar conversas' }))

    await waitFor(() => expect(posted).not.toBeNull())
    expect(posted!.from).toBeTruthy()
    expect(await screen.findByText(/2 processadas/)).toBeInTheDocument()
    expect(screen.getByText(/R\$\s?0,12/)).toBeInTheDocument()
  })

  // Síntese gerada a partir de leituras que já mudaram vem marcada: ler um parecer
  // desatualizado sem aviso é pior que não ter parecer.
  it('avisa quando a síntese está desatualizada', async () => {
    mswServer.use(
      analysesHandler(),
      http.get('/api/v1/ai/syntheses', () =>
        HttpResponse.json([
          {
            sellerId: 's1',
            sellerName: 'Ana',
            overview: 'amostra pequena',
            strengths: ['responde rápido'],
            improvements: ['não pede a venda'],
            dominantLossPattern: 'preço',
            trainingSuggestion: 'treinar fechamento',
            conversationsCount: 8,
            model: 'gemini-3.6-flash',
            createdAt: '2026-07-22T12:00:00Z',
            stale: true,
          },
        ]),
      ),
    )

    renderWithProviders(<AiAnalysisPage />)

    const panel = await screen.findByTestId('ai-syntheses')
    expect(within(panel).getByText('Desatualizada')).toBeInTheDocument()
    expect(within(panel).getByText('responde rápido')).toBeInTheDocument()
  })

  describe('no celular', () => {
    // Oito colunas nao cabem em 360px: cada conversa vira um card com os mesmos campos.
    it('troca a tabela por cards', async () => {
      mswServer.use(analysesHandler())

      renderMobile(<AiAnalysisPage />)

      const cards = await screen.findByTestId('analyses-cards')
      expect(screen.queryByTestId('analyses-table')).not.toBeInTheDocument()
      expect(within(cards).getByText('Maria')).toBeInTheDocument()
      expect(within(cards).getByText('Clientes perdidos')).toBeInTheDocument()
      expect(within(cards).getByText('Sim')).toBeInTheDocument()
    })

    // O detalhe (evidencia, mensagem sugerida) e o que torna a analise acionavel:
    // no card ele abre pelo mesmo botao de expandir.
    it('abre o detalhe dentro do card', async () => {
      mswServer.use(analysesHandler())

      renderMobile(<AiAnalysisPage />)
      const user = userEvent.setup()
      const cards = await screen.findByTestId('analyses-cards')

      expect(screen.queryByText(/achei caro/)).not.toBeInTheDocument()

      await user.click(within(cards).getByRole('button', { name: /ver o que a IA leu/ }))

      expect(screen.getByText(/achei caro/)).toBeInTheDocument()
      expect(screen.getByText(/consigo melhorar a condição/)).toBeInTheDocument()
    })

    // Sete campos de filtro na tela empurrariam a primeira analise para fora dela:
    // eles vao para uma folha, com a contagem dos ativos no botao.
    it('abre os filtros numa folha e conta os ativos', async () => {
      const ana = seller('Ana')
      let lastUrl: URL | null = null
      mswServer.use(
        http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
        analysesHandler((url) => {
          lastUrl = url
        }),
      )

      renderMobile(<AiAnalysisPage />)
      const user = userEvent.setup()
      await screen.findByTestId('analyses-cards')

      expect(screen.queryByTestId('ai-filters')).not.toBeInTheDocument()

      await user.click(screen.getByRole('button', { name: 'Filtros' }))
      await user.selectOptions(
        within(screen.getByTestId('ai-filters')).getByLabelText('Divergência'),
        'true',
      )

      await waitFor(() => expect(lastUrl!.searchParams.get('divergent')).toBe('true'))
      expect(screen.getByRole('button', { name: 'Filtros (1)' })).toBeInTheDocument()
    })
  })
})
