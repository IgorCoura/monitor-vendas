import { describe, expect, it } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ExportReportDialog } from './ExportReportDialog'
import { renderMobile, renderWithProviders } from '../../test/render'

const range = { from: '2026-07-01T03:00:00.000Z', to: '2026-07-31T02:59:59.000Z' }

function open(onClose: () => void = () => {}) {
  renderWithProviders(<ExportReportDialog open onClose={onClose} range={range} />)
}

function downloadUrl(): URL {
  const href = screen.getByTestId('export-download').getAttribute('href')!
  return new URL(href, 'http://localhost')
}

describe('ExportReportDialog', () => {
  // O que o usuário marca na tela é o que vai na URL do arquivo — métricas e
  // gráficos. A mesma métrica aparece nas duas listas, então a busca é escopada.
  it('leva a seleção de métricas e gráficos para a URL do download', async () => {
    open()
    const user = userEvent.setup()

    const metricChips = await screen.findByTestId('metric-chips')
    await user.click(await within(metricChips).findByRole('button', { name: 'Vendas' }))
    await user.click(within(screen.getByTestId('chart-chips')).getByRole('button', { name: 'Vendas' }))

    const url = downloadUrl()
    expect(url.pathname).toBe('/api/v1/reports/export')
    expect(url.searchParams.get('metrics')).toBe('sales')
    expect(url.searchParams.get('charts')).toBe('sales')
    expect(url.searchParams.get('from')).toBe(range.from)
  })

  // O relatório não tem mais nada de IA: nem opção, nem estimativa de custo, nem
  // espera por job — a planilha é fato medido e sai na hora.
  it('não oferece análise por IA nem custo', async () => {
    open()

    await screen.findByTestId('metric-chips')
    expect(screen.queryByLabelText('Incluir análise por IA')).not.toBeInTheDocument()
    expect(screen.queryByTestId('ai-estimate')).not.toBeInTheDocument()
    expect(screen.queryByText(/Custo estimado/)).not.toBeInTheDocument()
    expect(screen.getByTestId('export-download')).toHaveTextContent('Baixar planilha')
  })

  // Marcar "aba por número" é o default; desmarcar precisa chegar ao servidor,
  // senão a planilha sairia com uma aba que o usuário dispensou.
  it('manda includeNumbers=false quando a aba por número é desmarcada', async () => {
    open()
    const user = userEvent.setup()

    await user.click(await screen.findByLabelText('Incluir aba por número'))

    expect(downloadUrl().searchParams.get('includeNumbers')).toBe('false')
  })

  // Clicar em baixar fecha o dialog: o download é do navegador e não há mais
  // nada para acompanhar na tela.
  it('fecha o dialog ao baixar', async () => {
    let closed = false
    open(() => {
      closed = true
    })
    const user = userEvent.setup()

    await user.click(await screen.findByTestId('export-download'))

    expect(closed).toBe(true)
  })

  // Regressão: sem nada marcado todos os chips são renderizados e o dialog
  // crescia além da viewport, empurrando os botões para fora da tela (reportado
  // em 30/07/2026).
  //
  // jsdom não calcula layout — nenhum teste aqui mede pixels. O que este trava é
  // o mecanismo que impede o estouro: altura limitada, corpo rolável e rodapé de
  // ações grudado fora dele.
  it('não deixa o dialog crescer além da tela com todos os filtros abertos', async () => {
    open()

    // Pior caso: nada marcado, então nenhum chip é filtrado da lista.
    const metricChips = await screen.findByTestId('metric-chips')
    await within(metricChips).findByRole('button', { name: 'Vendas' })
    expect(within(metricChips).getAllByRole('button').length).toBeGreaterThan(0)
    expect(within(screen.getByTestId('chart-chips')).getAllByRole('button').length).toBeGreaterThan(0)

    expect(screen.getByRole('dialog').className).toMatch(/max-h-\[85vh\]/)

    const body = screen.getByTestId('dialog-body')
    expect(body.className).toMatch(/overflow-y-auto/)

    // Os botões vivem FORA da área rolável. Dentro dela, além de poderem sair da
    // tela, o rodapé cobria o último checkbox e roubava o clique dele.
    const actions = screen.getByTestId('export-actions')
    expect(body).not.toContainElement(actions)
    expect(screen.getByTestId('dialog-footer')).toContainElement(actions)
    expect(within(actions).getByTestId('export-download')).toBeInTheDocument()
    expect(within(actions).getByRole('button', { name: 'Cancelar' })).toBeInTheDocument()

    // O último campo do formulário continua clicável, dentro do corpo rolável.
    expect(body).toContainElement(screen.getByLabelText('Incluir aba por número'))
  })

  describe('no celular', () => {
    // As listas de chips começam fechadas: abertas, o botão de baixar ficava
    // dezenas de chips abaixo e o usuário não chegava nele.
    it('mantém o botão de baixar ao alcance, com os chips recolhidos', async () => {
      renderMobile(<ExportReportDialog open onClose={() => {}} range={range} />)

      expect(await screen.findByTestId('export-download')).toBeInTheDocument()
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
      await user.click(
        within(screen.getByTestId('metric-chips')).getByRole('button', { name: 'Vendas' }),
      )

      expect(screen.getByRole('button', { name: /Métricas\s*\(1\)/ })).toBeInTheDocument()
    })
  })
})
