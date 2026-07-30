import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { DashboardPage } from './DashboardPage'
import { renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer, rankingEntry } from '../../test/msw'

describe('DashboardPage', () => {
  // Ranking com dois vendedores: nomes aparecem na tabela e os KPIs somam os times.
  it('renderiza ranking e KPIs agregados do time', async () => {
    mswServer.use(
      http.get('/api/v1/reports/ranking', () =>
        HttpResponse.json([
          rankingEntry('Ana', {
            conversationsStarted: 10,
            conversationsAnswered: 8,
            responseRate: 0.8,
            sales: 4,
            conversionRate: 0.5,
            messagesSent: 100,
          }),
          rankingEntry('Bruno', {
            conversationsStarted: 10,
            conversationsAnswered: 4,
            responseRate: 0.4,
            sales: 1,
            conversionRate: 0.25,
            messagesSent: 50,
          }),
        ]),
      ),
    )

    renderWithProviders(<DashboardPage />)

    expect(await screen.findByText('Ana')).toBeInTheDocument()
    expect(screen.getByText('Bruno')).toBeInTheDocument()
    // 20 conversas no total; taxa do time = 12/20 = 60%; 5 vendas.
    expect(screen.getByText('20')).toBeInTheDocument()
    expect(screen.getByText('60%')).toBeInTheDocument()
    expect(screen.getByText('5')).toBeInTheDocument()
  })

  // Sem vendedores no período, mostra o estado vazio do ranking.
  it('mostra estado vazio quando não há dados', async () => {
    renderWithProviders(<DashboardPage />)

    expect(await screen.findByText('Nenhum vendedor com dados no período.')).toBeInTheDocument()
  })

  // Espera de resposta do time: média ponderada pela quantidade de respostas de cada
  // vendedor (10min×1 + 30min×3 = 25min), com mín dos mínimos e máx dos máximos no hint.
  it('agrega a espera de resposta ponderada pelo volume de cada vendedor', async () => {
    mswServer.use(
      http.get('/api/v1/reports/ranking', () =>
        HttpResponse.json([
          rankingEntry('Ana', {
            avgResponseMinutes: 10,
            minResponseMinutes: 5,
            maxResponseMinutes: 10,
            responseSamplesCount: 1,
          }),
          rankingEntry('Bruno', {
            avgResponseMinutes: 30,
            minResponseMinutes: 20,
            maxResponseMinutes: 60,
            responseSamplesCount: 3,
          }),
        ]),
      ),
    )

    renderWithProviders(<DashboardPage />)

    expect(await screen.findByText('25 min')).toBeInTheDocument()
    expect(screen.getByText('mín 5 min · máx 1h 00')).toBeInTheDocument()
  })

  // "Adicionar gráfico" empilha um novo gráfico já com a próxima métrica ainda não usada.
  it('adicionar gráfico usa a próxima métrica livre', async () => {
    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')

    await user.click(screen.getByRole('button', { name: '+ Adicionar gráfico' }))

    expect(screen.getByText('Ranking de vendedores — Conversão')).toBeInTheDocument()
    expect(screen.getByText('Ranking de vendedores — Taxa de resposta')).toBeInTheDocument()
  })

  // Métrica é exclusiva entre gráficos: escolher no gráfico 2 a métrica do gráfico 1 faz swap.
  it('selecionar métrica já usada em outro gráfico troca as duas', async () => {
    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')
    await user.click(screen.getByRole('button', { name: '+ Adicionar gráfico' }))

    const secondChart = screen.getByTestId('chart-1')
    await user.click(within(secondChart).getByRole('button', { name: 'Conversão' }))

    expect(within(screen.getByTestId('chart-0')).getByText('Ranking de vendedores — Taxa de resposta')).toBeInTheDocument()
    expect(within(screen.getByTestId('chart-1')).getByText('Ranking de vendedores — Conversão')).toBeInTheDocument()
  })

  // Personalizar: desmarcar uma métrica global remove o card do dashboard.
  it('permite ocultar métricas globais dos cards', async () => {
    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')
    expect(screen.getByTestId('kpi-vendas')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Personalizar' }))
    await user.click(within(screen.getByTestId('customize-kpis')).getByLabelText('Vendas'))

    expect(screen.queryByTestId('kpi-vendas')).not.toBeInTheDocument()
  })

  // O "Personalizar" do topo cuida SÓ das métricas globais: colunas e organização
  // dos gráficos têm botões próprios no contexto de cada bloco.
  it('o dialog de métricas globais não traz colunas nem organização', async () => {
    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')

    await user.click(screen.getByRole('button', { name: 'Personalizar' }))

    expect(screen.getByTestId('customize-kpis')).toBeInTheDocument()
    expect(screen.queryByTestId('customize-columns')).not.toBeInTheDocument()
    expect(screen.queryByTestId('customize-layout')).not.toBeInTheDocument()
  })

  // Botão no cabeçalho de "Todos os índices" oculta coluna sem afetar os cards.
  it('permite ocultar colunas da lista de funcionários', async () => {
    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')
    const table = screen.getByTestId('ranking-table')
    expect(within(table).getByText('Bans')).toBeInTheDocument()

    await user.click(within(table).getByRole('button', { name: 'Personalizar colunas' }))
    await user.click(within(screen.getByTestId('customize-columns')).getByLabelText('Bans'))

    expect(within(screen.getByTestId('ranking-table')).queryByText('Bans')).not.toBeInTheDocument()
    expect(screen.getByTestId('kpi-vendas')).toBeInTheDocument()
  })

  // Botão de organização (ao lado de "+ Adicionar gráfico") alterna lista/grade 2/grade 3.
  it('altera a organização dos gráficos para grade', async () => {
    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')
    expect(screen.getByTestId('charts-container').className).not.toContain('md:grid-cols-2')

    await user.click(screen.getByRole('button', { name: 'Organização dos gráficos' }))
    await user.click(screen.getByLabelText('Grade (2 colunas)'))

    expect(screen.getByTestId('charts-container').className).toContain('md:grid-cols-2')

    await user.click(screen.getByLabelText('Grade (3 colunas)'))
    expect(screen.getByTestId('charts-container').className).toContain('xl:grid-cols-3')
  })

  // Gráficos adicionados sobrevivem ao refresh (persistidos no navegador).
  it('mantém os gráficos adicionados após recarregar', async () => {
    const user = userEvent.setup()
    const first = renderWithProviders(<DashboardPage />)
    await screen.findByText('Ranking de vendedores — Conversão')
    await user.click(screen.getByRole('button', { name: '+ Adicionar gráfico' }))
    expect(screen.getByText('Ranking de vendedores — Taxa de resposta')).toBeInTheDocument()

    first.unmount()
    renderWithProviders(<DashboardPage />)

    expect(await screen.findByText('Ranking de vendedores — Conversão')).toBeInTheDocument()
    expect(screen.getByText('Ranking de vendedores — Taxa de resposta')).toBeInTheDocument()
  })

  // O período escolhido também sobrevive ao refresh.
  it('mantém o período escolhido após recarregar', async () => {
    const user = userEvent.setup()
    const first = renderWithProviders(<DashboardPage />)
    await screen.findByText('Ranking de vendedores — Conversão')
    await user.click(screen.getByRole('button', { name: 'Hoje' }))

    first.unmount()
    renderWithProviders(<DashboardPage />)

    expect(await screen.findByRole('button', { name: 'Hoje', pressed: true })).toBeInTheDocument()
  })

  // A barra de atualização mostra a data/hora da última busca (não fica em "—").
  it('exibe o horário da última atualização', async () => {
    renderWithProviders(<DashboardPage />)

    await screen.findByText('Ranking de vendedores — Conversão')
    await waitFor(() =>
      expect(screen.getByTestId('last-poll').textContent).toMatch(/Atualizado \d{2}\/\d{2}/),
    )
  })

  // O botão de refresh dispara uma nova busca na API na hora.
  it('atualiza manualmente ao clicar no ícone de refresh', async () => {
    let calls = 0
    mswServer.use(
      http.get('/api/v1/reports/ranking', () => {
        calls += 1
        return HttpResponse.json([])
      }),
    )

    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()
    await screen.findByText('Ranking de vendedores — Conversão')
    await waitFor(() => expect(calls).toBe(1))

    await user.click(screen.getByRole('button', { name: 'Atualizar agora' }))

    await waitFor(() => expect(calls).toBe(2))
  })

  // A escolha do intervalo de atualização automática persiste após recarregar.
  it('mantém o intervalo de atualização escolhido', async () => {
    const user = userEvent.setup()
    const first = renderWithProviders(<DashboardPage />)
    await screen.findByText('Ranking de vendedores — Conversão')

    await user.click(screen.getByRole('button', { name: 'Desligar atualização automática' }))
    first.unmount()
    renderWithProviders(<DashboardPage />)

    expect(
      await screen.findByRole('button', { name: 'Desligar atualização automática', pressed: true }),
    ).toBeInTheDocument()
  })

  // Tipos de desfecho do servidor viram card, coluna e opção de gráfico sozinhos —
  // "Clientes perdidos" aparece sem nenhum código específico dele no dashboard.
  it('gera card, coluna e gráfico para cada tipo de desfecho', async () => {
    mswServer.use(
      http.get('/api/v1/reports/ranking', () =>
        HttpResponse.json([
          rankingEntry('Ana', {
            conversationsAnswered: 4,
            outcomes: [
              { typeCode: 'sale', name: 'Vendas', count: 2, rate: 0.5, avgTimeToCloseBusinessHours: 3 },
              { typeCode: 'lost', name: 'Clientes perdidos', count: 3, rate: 0.75, avgTimeToCloseBusinessHours: 5 },
            ],
          }),
        ]),
      ),
    )

    renderWithProviders(<DashboardPage />)
    const user = userEvent.setup()

    const card = await screen.findByTestId('kpi-outcome:lost')
    expect(within(card).getByText('Clientes perdidos')).toBeInTheDocument()
    expect(within(card).getByText('3')).toBeInTheDocument()

    expect(within(screen.getByTestId('ranking-table')).getByText('Clientes perdidos')).toBeInTheDocument()

    await user.click(within(screen.getByTestId('chart-0')).getByRole('button', { name: 'Clientes perdidos' }))
    expect(screen.getByText('Ranking de vendedores — Clientes perdidos')).toBeInTheDocument()
  })

  // Cada métrica tem o "?" com a explicação acessível (aria-label do tooltip).
  it('exibe os tooltips de explicação das métricas', async () => {
    renderWithProviders(<DashboardPage />)
    await screen.findByText('Ranking de vendedores — Conversão')

    const tips = screen.getAllByRole('img', { name: /conversas|vendas|etiqueta|horas úteis/i })
    expect(tips.length).toBeGreaterThan(3)
  })
})
