import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AiAnalysisPage } from './AiAnalysisPage'
import { renderMobile, renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer, seller } from '../../test/msw'
import type { AiAnalysisRowDto, AiJobDto } from '../../api/types'

function row(overrides: Partial<AiAnalysisRowDto> = {}): AiAnalysisRowDto {
  return {
    conversationId: crypto.randomUUID(),
    analysisId: crypto.randomUUID(),
    sellerId: 's1',
    sellerName: 'Ana',
    sellerNumber: '5511900001111',
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
    audioExpected: 0,
    audioAttached: 0,
    ...overrides,
  }
}

function job(overrides: Partial<AiJobDto> = {}): AiJobDto {
  return {
    id: 'job-1',
    kind: 'Analyze',
    status: 'Completed',
    total: 2,
    processed: 2,
    skipped: 0,
    costBrl: 0.12,
    error: null,
    createdAt: '2026-07-22T11:00:00Z',
    completedAt: '2026-07-22T12:00:00Z',
    ...overrides,
  }
}

function statusHandler(overrides: Partial<{
  running: AiJobDto | null
  lastAnalysis: AiJobDto | null
  lastSynthesis: AiJobDto | null
}> = {}) {
  return http.get('/api/v1/ai/status', () =>
    HttpResponse.json({ running: null, lastAnalysis: null, lastSynthesis: null, ...overrides }),
  )
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

  // Áudio que a IA não ouviu deixa a leitura incompleta: sem aviso, ela fica
  // idêntica a uma leitura completa e o usuário culpa o modelo por uma falha de
  // download (foi o que aconteceu em 31/07/2026).
  it('avisa quando a IA não ouviu todos os áudios', async () => {
    const incompleta = row({ audioExpected: 5, audioAttached: 2 })
    mswServer.use(analysesHandler(undefined, [incompleta]))

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()

    const aviso = await screen.findByTestId(`audio-warning-${incompleta.analysisId}`)
    expect(aviso).toHaveTextContent('3 de 5 áudios não lidos')

    // O detalhe explica o que aconteceu, sem o usuário ter que adivinhar.
    await user.click(within(screen.getByTestId('analyses-table')).getByText('Maria'))
    expect(await screen.findByText(/Áudios ouvidos:/)).toBeInTheDocument()
    expect(screen.getByText(/não puderam ser baixados/)).toBeInTheDocument()
  })

  // Leitura completa (ou conversa sem áudio) não ganha aviso nenhum: alarme que
  // toca sempre deixa de ser lido.
  it('não avisa quando todos os áudios foram ouvidos', async () => {
    mswServer.use(analysesHandler(undefined, [row({ audioExpected: 2, audioAttached: 2 })]))

    renderWithProviders(<AiAnalysisPage />)

    await screen.findByTestId('analyses-table')
    expect(screen.queryByText(/áudios não lidos/)).not.toBeInTheDocument()
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

  // Antes de gastar, o custo estimado aparece; confirmar dispara o job e a tela
  // avisa que ele roda em segundo plano, em vez de mostrar progresso.
  it('mostra o custo e confirma que a rodada começou', async () => {
    let posted: { from: string } | null = null
    mswServer.use(
      analysesHandler(),
      http.post('/api/v1/ai/analyses/run', async ({ request }) => {
        posted = (await request.json()) as { from: string }
        return HttpResponse.json(job({ status: 'Pending', completedAt: null }), { status: 202 })
      }),
    )

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await screen.findByTestId('analyses-table')

    await user.click(screen.getByRole('button', { name: 'Analisar conversas' }))

    const box = await screen.findByTestId('ai-estimate')
    expect(box).toHaveTextContent('Custo estimado R$ 2,40')
    expect(box).toHaveTextContent('saldo R$ 17,60')
    // Só o que mudou custa: o que foi reaproveitado aparece para o custo baixo
    // não parecer erro.
    expect(box).toHaveTextContent('8 sem leitura ou com mensagem nova')
    expect(box).toHaveTextContent('4 reaproveitadas (não custam nada)')

    await user.click(screen.getByRole('button', { name: 'Confirmar' }))

    await waitFor(() => expect(posted).not.toBeNull())
    expect(posted!.from).toBeTruthy()
    const started = await screen.findByTestId('ai-started')
    expect(started).toHaveTextContent('Análise iniciada.')
    expect(started).toHaveTextContent(/Pode fechar esta tela/)
  })

  // Reprocessar tudo ignorando o cache existe na API, mas não na tela: a rodada
  // pedida daqui nunca manda `force`.
  it('não oferece reanalisar o que não mudou', async () => {
    let posted: Record<string, unknown> | null = null
    mswServer.use(
      analysesHandler(),
      http.post('/api/v1/ai/analyses/run', async ({ request }) => {
        posted = (await request.json()) as Record<string, unknown>
        return HttpResponse.json(job({ status: 'Pending', completedAt: null }), { status: 202 })
      }),
    )

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await screen.findByTestId('analyses-table')

    expect(screen.queryByRole('button', { name: /ignorando o cache|tudo/i })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Analisar conversas' }))
    await screen.findByTestId('ai-estimate')
    await user.click(screen.getByRole('button', { name: 'Confirmar' }))

    await waitFor(() => expect(posted).not.toBeNull())
    expect(posted!.force).toBeUndefined()
  })

  // Saldo insuficiente trava a confirmação: bater no erro do servidor depois só
  // faria o usuário esperar para nada.
  it('bloqueia a confirmação quando o saldo não cobre', async () => {
    mswServer.use(
      analysesHandler(),
      http.post('/api/v1/ai/estimate', () =>
        HttpResponse.json({
          conversations: 12,
          cached: 0,
          sellers: 0,
          estimatedBrl: 40,
          available: 5,
          affordable: false,
          budgetEnabled: true,
          truncated: false,
        }),
      ),
    )

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await screen.findByTestId('analyses-table')

    await user.click(screen.getByRole('button', { name: 'Analisar conversas' }))

    expect(await screen.findByText(/O saldo da janela não cobre/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar' })).toBeDisabled()
  })

  // Com uma rodada em andamento os dois botões ficam travados e a tela diz o que
  // está acontecendo. O estado vem do servidor, então sobrevive a recarregar.
  it('trava os dois botões enquanto uma rodada está em andamento', async () => {
    mswServer.use(
      analysesHandler(),
      statusHandler({ running: job({ status: 'Running', kind: 'Synthesize', completedAt: null }) }),
    )

    renderWithProviders(<AiAnalysisPage />)

    expect(await screen.findByTestId('ai-running')).toHaveTextContent('Síntese em andamento')
    expect(screen.getByRole('button', { name: 'Analisar conversas' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Refazer síntese' })).toBeDisabled()
  })

  // As datas da última análise e da última síntese são separadas: refazer a
  // síntese é barato e acontece com outra frequência.
  it('mostra as datas das últimas rodadas separadas', async () => {
    mswServer.use(
      analysesHandler(),
      statusHandler({
        lastAnalysis: job({ completedAt: '2026-07-22T12:00:00Z' }),
        lastSynthesis: null,
      }),
    )

    renderWithProviders(<AiAnalysisPage />)

    const line = await screen.findByTestId('ai-last-runs')
    await waitFor(() => expect(line).toHaveTextContent('Última análise: 22/07, 09:00'))
    expect(line).toHaveTextContent('Última síntese: —')
  })

  // Rodada recusada por saldo termina no servidor: o motivo precisa aparecer na
  // tela, senão o usuário só vê que nada aconteceu.
  it('mostra o motivo quando a última rodada falhou', async () => {
    mswServer.use(
      analysesHandler(),
      statusHandler({
        lastAnalysis: job({ status: 'Failed', error: 'Análise não realizada por falta de saldo.' }),
      }),
    )

    renderWithProviders(<AiAnalysisPage />)

    expect(
      await screen.findByText(/Última análise: Análise não realizada por falta de saldo\./),
    ).toBeInTheDocument()
  })

  // O botão de exportar leva os filtros da tela: a planilha é o que está sendo
  // visto, não a base inteira.
  it('exporta as análises com os filtros da tela', async () => {
    mswServer.use(analysesHandler())

    renderWithProviders(<AiAnalysisPage />)
    const user = userEvent.setup()
    await screen.findByTestId('analyses-table')

    await user.selectOptions(screen.getByLabelText('Divergência'), 'true')

    await waitFor(() => {
      const url = new URL(screen.getByTestId('ai-export').getAttribute('href')!, 'http://localhost')
      expect(url.pathname).toBe('/api/v1/ai/analyses/export')
      expect(url.searchParams.get('divergent')).toBe('true')
    })
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
