import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ExportReportDialog } from './ExportReportDialog'
import { renderMobile, renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer } from '../../test/msw'
import type { ReportExportDto, ReportExportFilters } from '../../api/types'

const range = { from: '2026-07-01T03:00:00.000Z', to: '2026-07-31T02:59:59.000Z' }

function job(overrides: Partial<ReportExportDto> = {}): ReportExportDto {
  return {
    id: 'exp-1',
    from: range.from,
    to: range.to,
    status: 'Completed',
    totalConversations: 12,
    analyzedConversations: 10,
    cachedConversations: 2,
    skippedConversations: 0,
    costBrl: 0.42,
    phase: null,
    error: null,
    fileName: 'relatorio.xlsx',
    fileAvailable: true,
    createdAt: range.from,
    completedAt: range.to,
    ...overrides,
  }
}

function estimateHandler(overrides: Record<string, unknown> = {}) {
  return http.post('/api/v1/reports/export/estimate', () =>
    HttpResponse.json({
      conversations: 12,
      cached: 2,
      toAnalyze: 10,
      estimatedBrl: 2.4,
      available: 17.6,
      affordable: true,
      truncated: false,
      ...overrides,
    }),
  )
}

function open() {
  renderWithProviders(<ExportReportDialog open onClose={() => {}} range={range} />)
}

describe('ExportReportDialog', () => {
  // O que o usuário marca na tela é o que vai para a API — métricas e gráficos.
  // A mesma métrica aparece nas duas listas, então a busca é escopada.
  it('envia a seleção de métricas e gráficos', async () => {
    let posted: ReportExportFilters | null = null
    mswServer.use(
      http.post('/api/v1/reports/export', async ({ request }) => {
        posted = (await request.json()) as ReportExportFilters
        return HttpResponse.json(job({ status: 'Pending', fileAvailable: false }), { status: 202 })
      }),
      http.get('/api/v1/reports/export/exp-1', () => HttpResponse.json(job())),
    )

    open()
    const user = userEvent.setup()
    await user.click(within(await screen.findByTestId('metric-chips')).getByRole('button', { name: 'Vendas' }))
    await user.click(within(screen.getByTestId('chart-chips')).getByRole('button', { name: 'Vendas' }))
    await user.click(screen.getByRole('button', { name: 'Gerar planilha' }))

    await waitFor(() => expect(posted).not.toBeNull())
    expect(posted!.metrics).toEqual(['sales'])
    expect(posted!.charts).toEqual(['sales'])
    expect(posted!.includeAi).toBe(false)
    expect(posted!.from).toBe(range.from)
  })

  // Com a IA ligada, o custo estimado e o saldo aparecem antes de confirmar.
  it('mostra o custo estimado e o saldo quando a IA é marcada', async () => {
    mswServer.use(estimateHandler())

    open()
    const user = userEvent.setup()
    await user.click(await screen.findByLabelText('Incluir análise por IA'))

    const box = await screen.findByTestId('ai-estimate')
    expect(box).toHaveTextContent('Custo estimado R$ 2,40')
    expect(box).toHaveTextContent('saldo R$ 17,60')
    expect(box).toHaveTextContent('2 já analisadas')
  })

  // Saldo insuficiente bloqueia a geração em vez de deixar o job falhar depois.
  it('bloqueia a geração quando o saldo não cobre', async () => {
    mswServer.use(estimateHandler({ affordable: false, estimatedBrl: 40, available: 5 }))

    open()
    const user = userEvent.setup()
    await user.click(await screen.findByLabelText('Incluir análise por IA'))

    expect(await screen.findByText(/O saldo da janela não cobre/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Gerar planilha' })).toBeDisabled()
  })

  // O progresso é acompanhado até o fim e o download aparece só quando existe arquivo.
  it('acompanha o job e oferece o download no final', async () => {
    mswServer.use(
      http.post('/api/v1/reports/export', () =>
        HttpResponse.json(job({ status: 'Pending', fileAvailable: false }), { status: 202 }),
      ),
      http.get('/api/v1/reports/export/exp-1', () => HttpResponse.json(job())),
    )

    open()
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Gerar planilha' }))

    expect(await screen.findByText('Planilha pronta.')).toBeInTheDocument()
    expect(await screen.findByTestId('export-download')).toHaveAttribute(
      'href',
      '/api/v1/reports/export/exp-1/file',
    )
    expect(screen.getByText(/10 analisadas agora/)).toBeInTheDocument()
  })

  // Regressão: sem nada marcado todos os chips são renderizados e, com a caixa de
  // IA aberta por cima, o dialog crescia além da viewport e empurrava os botões
  // para fora da tela (reportado em 30/07/2026).
  //
  // jsdom não calcula layout — nenhum teste aqui mede pixels. O que este trava é o
  // mecanismo que impede o estouro: altura limitada, corpo rolável e rodapé de
  // ações grudado dentro dele.
  it('não deixa o dialog crescer além da tela com todos os filtros abertos', async () => {
    mswServer.use(estimateHandler())

    open()
    const user = userEvent.setup()
    await user.click(await screen.findByLabelText('Incluir análise por IA'))
    await screen.findByTestId('ai-estimate')

    // Pior caso: nada marcado, então nenhum chip é filtrado da lista.
    expect(within(screen.getByTestId('metric-chips')).getAllByRole('button').length).toBeGreaterThan(0)
    expect(within(screen.getByTestId('chart-chips')).getAllByRole('button').length).toBeGreaterThan(0)

    expect(screen.getByRole('dialog').className).toMatch(/max-h-\[85vh\]/)

    const body = screen.getByTestId('dialog-body')
    expect(body.className).toMatch(/overflow-y-auto/)

    // Os botões vivem FORA da área rolável. Dentro dela, além de poderem sair da
    // tela, o rodapé cobria o último checkbox e roubava o clique dele.
    const actions = screen.getByTestId('export-actions')
    expect(body).not.toContainElement(actions)
    expect(screen.getByTestId('dialog-footer')).toContainElement(actions)
    expect(within(actions).getByRole('button', { name: 'Gerar planilha' })).toBeInTheDocument()
    expect(within(actions).getByRole('button', { name: 'Cancelar' })).toBeInTheDocument()

    // O último campo do formulário continua clicável, dentro do corpo rolável.
    expect(body).toContainElement(screen.getByLabelText('Incluir análise por IA'))
  })

  // A fase da síntese espera cota do provedor e pode levar minutos: a tela precisa
  // dizer o que está acontecendo, senão parece travada (foi o que aconteceu).
  it('mostra a fase em andamento enquanto o job roda', async () => {
    mswServer.use(
      http.post('/api/v1/reports/export', () =>
        HttpResponse.json(job({ status: 'Running', fileAvailable: false, phase: 'Sintetizando vendedores' }), { status: 202 }),
      ),
      http.get('/api/v1/reports/export/exp-1', () =>
        HttpResponse.json(job({ status: 'Running', fileAvailable: false, phase: 'Sintetizando vendedores' })),
      ),
    )

    open()
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Gerar planilha' }))

    expect(await screen.findByText(/Sintetizando vendedores/)).toBeInTheDocument()
    expect(screen.getByText(/limite de chamadas da IA/)).toBeInTheDocument()
  })

  // Job que falhou mostra o erro do servidor, não some em silêncio.
  it('mostra o erro quando a exportação falha', async () => {
    mswServer.use(
      http.post('/api/v1/reports/export', () =>
        HttpResponse.json(job({ status: 'Pending', fileAvailable: false }), { status: 202 }),
      ),
      http.get('/api/v1/reports/export/exp-1', () =>
        HttpResponse.json(job({ status: 'Failed', fileAvailable: false, error: 'Modelo sem preço.' })),
      ),
    )

    open()
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Gerar planilha' }))

    expect(await screen.findByText('Modelo sem preço.')).toBeInTheDocument()
  })

  describe('no celular', () => {
    // As listas de chips começam fechadas: abertas, "Gerar planilha" ficava
    // dezenas de chips abaixo e o usuário não chegava nele.
    it('mantém o botão de gerar ao alcance, com os chips recolhidos', async () => {
      renderMobile(<ExportReportDialog open onClose={() => {}} range={range} />)

      expect(await screen.findByRole('button', { name: 'Gerar planilha' })).toBeInTheDocument()
      expect(screen.queryByTestId('metric-chips')).not.toBeInTheDocument()
      // O rodapé de ações fica fora do corpo rolável do dialog.
      expect(screen.getByTestId('dialog-body')).not.toContainElement(
        screen.getByTestId('export-actions'),
      )
    })

    // A seção abre no toque e mostra quantos itens foram marcados nela.
    it('abre a seção de métricas e marca a contagem escolhida', async () => {
      renderMobile(<ExportReportDialog open onClose={() => {}} range={range} />)
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: /^Métricas/ }))
      await user.click(within(screen.getByTestId('metric-chips')).getByRole('button', { name: 'Vendas' }))

      expect(screen.getByRole('button', { name: /Métricas\s*\(1\)/ })).toBeInTheDocument()
    })
  })
})
