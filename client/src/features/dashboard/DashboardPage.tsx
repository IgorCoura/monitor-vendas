import { useMemo, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { useRanking } from '../../api/queries'
import type { RankingEntryDto } from '../../api/types'
import { KpiCard } from '../../components/KpiCard'
import { Button, Card, Dialog, EmptyState, ErrorState, InfoTip, Select, Spinner } from '../../components/ui'
import { MobilePeriodBar } from '../../components/mobile/PeriodBar'
import { ExpandableMetricCard, type MetricItem } from '../../components/mobile/MetricList'
import { useIsMobile } from '../../lib/useIsMobile'
import { fmtDateTime, fmtMinutes, fmtPercent, fmtPerHour, periodOptions } from '../../lib/format'
import { chartInk, chartSeries } from '../../lib/palette'
import {
  chartMetricByKey,
  chartMetrics,
  chartMetricsFor,
  formatChartValue,
  metricHelp,
  outcomeCount,
  outcomeHelp,
  sanitizeChartKeys,
} from '../../lib/metrics'
import { usePersistedState } from '../../lib/usePersistedState'
import { usePeriodRange } from '../../lib/usePeriodRange'
import { usePollMs } from '../../lib/polling'
import { UpdateControls } from '../../components/UpdateControls'
import { ExportReportDialog } from '../reports/ExportReportDialog'

type ChartLayout = 'list' | 'grid2' | 'grid3'

const chartLayoutOptions: { value: ChartLayout; label: string }[] = [
  { value: 'list', label: 'Lista' },
  { value: 'grid2', label: 'Grade (2 colunas)' },
  { value: 'grid3', label: 'Grade (3 colunas)' },
]

const chartLayoutClass: Record<ChartLayout, string> = {
  list: 'grid grid-cols-1 gap-6',
  grid2: 'grid grid-cols-1 gap-6 md:grid-cols-2',
  grid3: 'grid grid-cols-1 gap-6 md:grid-cols-2 xl:grid-cols-3',
}

function GridIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" className="h-3.5 w-3.5" fill="currentColor">
      <rect x="1" y="1" width="6" height="6" rx="1.5" />
      <rect x="9" y="1" width="6" height="6" rx="1.5" />
      <rect x="1" y="9" width="6" height="6" rx="1.5" />
      <rect x="9" y="9" width="6" height="6" rx="1.5" />
    </svg>
  )
}

// Colunas da lista de funcionários; a visibilidade é escolhida pelo usuário
// no botão de personalização do próprio card (coluna Vendedor é fixa).
const tableColumns: {
  key: string
  label: string
  help?: string
  nowrap?: boolean
  render: (e: RankingEntryDto) => ReactNode
}[] = [
  { key: 'conversas', label: 'Conversas', help: metricHelp.conversas, render: (e) => e.metrics.conversationsStarted },
  { key: 'resposta', label: 'Resposta', help: metricHelp.resposta, render: (e) => fmtPercent(e.metrics.responseRate) },
  { key: 'espera', label: 'Espera méd.', help: metricHelp.espera, nowrap: true, render: (e) => fmtMinutes(e.metrics.avgResponseMinutes) },
  { key: 'naoresp', label: 'Não resp.', help: metricHelp.naoRespondidas, render: (e) => e.metrics.conversationsUnanswered },
  { key: 'disparos', label: 'Disparos', help: metricHelp.disparos, render: (e) => e.metrics.outboundConversationsStarted },
  { key: 'captacoes', label: 'Captações', help: metricHelp.captacoes, render: (e) => e.metrics.outboundConversationsEngaged },
  { key: 'vendas', label: 'Vendas', help: metricHelp.vendas, render: (e) => e.metrics.sales },
  { key: 'conversao', label: 'Conversão', help: metricHelp.conversao, render: (e) => fmtPercent(e.metrics.conversionRate) },
  { key: 'followup', label: 'Follow-up', help: metricHelp.followUp, render: (e) => fmtPercent(e.metrics.followUpRate) },
  { key: 'medenvh', label: 'Méd. env./h', help: metricHelp.mediaEnviadas, render: (e) => fmtPerHour(e.metrics.avgSentPerBusinessHour) },
  { key: 'ultenvio', label: 'Últ. envio', help: metricHelp.ultimoEnvio, nowrap: true, render: (e) => fmtDateTime(e.metrics.lastOutboundMessageAt) },
  { key: 'uptime', label: 'Uptime', help: metricHelp.uptime, render: (e) => `${e.metrics.uptimePercent.toFixed(0)}%` },
  { key: 'bans', label: 'Bans', help: metricHelp.bans, render: (e) => e.metrics.banCount },
]

function toggleHidden(list: string[], key: string): string[] {
  return list.includes(key) ? list.filter((k) => k !== key) : [...list, key]
}

function VisibilityChecklist({
  items,
  hidden,
  onToggle,
}: {
  items: { key: string; label: string }[]
  hidden: string[]
  onToggle: (key: string) => void
}) {
  return (
    // Uma coluna no celular: duas colunas de checkbox em 328px deixam o alvo de
    // toque menor que o dedo e cortam rótulos como "Média rec./h".
    <div className="grid grid-cols-1 gap-1.5 md:grid-cols-2">
      {items.map((item) => (
        <label key={item.key} className="flex min-h-11 items-center gap-2 text-sm md:min-h-0">
          <input
            type="checkbox"
            className="h-4 w-4 accent-primary"
            checked={!hidden.includes(item.key)}
            onChange={() => onToggle(item.key)}
          />
          {item.label}
        </label>
      ))}
    </div>
  )
}

function RankingChartCard({
  index,
  metricKey,
  ranking,
  availableMetrics,
  canRemove,
  isMobile,
  onSelect,
  onRemove,
}: {
  index: number
  metricKey: string
  ranking: RankingEntryDto[]
  availableMetrics: typeof chartMetrics
  canRemove: boolean
  isMobile: boolean
  onSelect: (key: string) => void
  onRemove: () => void
}) {
  const def = chartMetricByKey(metricKey, availableMetrics)
  const data = ranking
    .map((e) => ({ name: e.name, value: def.value(e.metrics) }))
    .sort((a, b) => b.value - a.value)

  return (
    <Card data-testid={`chart-${index}`}>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <h3 className="text-sm font-semibold">Ranking de vendedores — {def.label}</h3>
        {/* No celular a escolha da métrica não pode ser um botão por métrica:
            com os tipos de desfecho são 15+ botões dentro do card. Vira um
            <select> nativo, que o telefone abre como roda de rolagem. */}
        {isMobile ? (
          <div className="flex w-full items-center gap-2">
            <Select
              aria-label={`Métrica do gráfico ${index + 1}`}
              value={metricKey}
              onChange={(e) => onSelect(e.target.value)}
              className="min-w-0 flex-1"
            >
              {availableMetrics.map((m) => (
                <option key={m.key} value={m.key}>
                  {m.label}
                </option>
              ))}
            </Select>
            {canRemove && (
              <Button
                variant="ghost"
                aria-label={`Remover gráfico ${index + 1}`}
                onClick={onRemove}
                className="shrink-0"
              >
                ✕
              </Button>
            )}
          </div>
        ) : (
          <div className="flex flex-wrap items-center gap-1">
            {availableMetrics.map((m) => (
              <Button
                key={m.key}
                variant={m.key === metricKey ? 'primary' : 'ghost'}
                onClick={() => onSelect(m.key)}
              >
                {m.label}
              </Button>
            ))}
            {canRemove && (
              <Button variant="ghost" aria-label={`Remover gráfico ${index + 1}`} onClick={onRemove}>
                ✕
              </Button>
            )}
          </div>
        )}
      </div>

      {data.length === 0 ? (
        <EmptyState message="Nenhum vendedor com dados no período." />
      ) : (
        // O eixo de nomes ocupava 140px fixos — quase metade da largura útil de
        // um celular. Abaixo de `md` ele encolhe e as barras voltam a ter espaço.
        <ResponsiveContainer
          width="100%"
          height={Math.max(120, data.length * (isMobile ? 38 : 44))}
        >
          <BarChart
            data={data}
            layout="vertical"
            margin={isMobile ? { left: 0, right: 12 } : { left: 8, right: 24 }}
          >
            <CartesianGrid horizontal={false} stroke={chartInk.grid} />
            <XAxis
              type="number"
              tick={{ fill: chartInk.axis, fontSize: isMobile ? 10 : 12 }}
              tickFormatter={(v: number) => (def.percent ? `${v.toFixed(0)}%` : String(v))}
              axisLine={false}
              tickLine={false}
            />
            <YAxis
              type="category"
              dataKey="name"
              width={isMobile ? 88 : 140}
              tick={{ fill: chartInk.axis, fontSize: isMobile ? 10 : 12 }}
              axisLine={false}
              tickLine={false}
            />
            <Tooltip
              formatter={(v) => formatChartValue(def, Number(v))}
              contentStyle={{ background: chartInk.tooltipBg, borderRadius: 8 }}
            />
            <Bar
              dataKey="value"
              fill={chartSeries.primary}
              barSize={isMobile ? 14 : 16}
              radius={[0, 4, 4, 0]}
            />
          </BarChart>
        </ResponsiveContainer>
      )}
    </Card>
  )
}

function Th({ label, help }: { label: string; help?: string }) {
  return (
    <th className="py-2 pr-4 whitespace-nowrap">
      <span className="inline-flex items-center gap-1">
        {label}
        {help && <InfoTip text={help} />}
      </span>
    </th>
  )
}

export function DashboardPage() {
  const isMobile = useIsMobile()
  const [pollMs, setPollMs] = usePollMs()
  const { period, setPeriod, range, refreshNow } = usePeriodRange(pollMs)
  const [charts, setCharts] = usePersistedState<string[]>(
    'mv:dash:charts',
    ['conversion'],
    (value) => sanitizeChartKeys(value, chartMetrics),
  )
  const [actionsOpen, setActionsOpen] = useState(false)
  const [kpisDialogOpen, setKpisDialogOpen] = useState(false)
  const [columnsDialogOpen, setColumnsDialogOpen] = useState(false)
  const [layoutDialogOpen, setLayoutDialogOpen] = useState(false)
  const [hiddenKpis, setHiddenKpis] = usePersistedState<string[]>('mv:dash:hiddenKpis', [])
  const [hiddenColumns, setHiddenColumns] = usePersistedState<string[]>('mv:dash:hiddenColumns', [])
  const [chartLayout, setChartLayout] = usePersistedState<ChartLayout>('mv:dash:chartLayout', 'list')

  const freshRef = useRef(false)
  const {
    data: ranking,
    isLoading,
    isError,
    isFetching,
    dataUpdatedAt,
    refetch,
  } = useRanking(range, pollMs, freshRef)

  function refreshManually() {
    freshRef.current = true
    refreshNow()
    void refetch()
  }

  // Totais do time recalculados a partir das somas (média de taxas seria errada);
  // médias por hora e espera reagregadas por soma/ponderação, nunca média de médias.
  const totals = useMemo(() => {
    const entries = ranking ?? []
    const sum = (f: (e: RankingEntryDto) => number) => entries.reduce((s, e) => s + f(e), 0)
    const started = sum((e) => e.metrics.conversationsStarted)
    const answered = sum((e) => e.metrics.conversationsAnswered)
    const sales = sum((e) => e.metrics.sales)
    const sent = sum((e) => e.metrics.messagesSent)
    const received = sum((e) => e.metrics.messagesReceived)
    const hours = sum((e) => e.metrics.effectiveBusinessHours)

    const withSamples = entries.filter((e) => e.metrics.responseSamplesCount > 0)
    const sampleCount = sum((e) => e.metrics.responseSamplesCount)
    const waitMin = withSamples.length
      ? Math.min(...withSamples.map((e) => e.metrics.minResponseMinutes ?? Infinity))
      : null
    const waitMax = withSamples.length
      ? Math.max(...withSamples.map((e) => e.metrics.maxResponseMinutes ?? -Infinity))
      : null
    const waitAvg =
      sampleCount > 0
        ? withSamples.reduce(
            (s, e) => s + (e.metrics.avgResponseMinutes ?? 0) * e.metrics.responseSamplesCount,
            0,
          ) / sampleCount
        : null

    return {
      waitMin,
      waitMax,
      waitAvg,
      started,
      unanswered: sum((e) => e.metrics.conversationsUnanswered),
      shots: sum((e) => e.metrics.outboundConversationsStarted),
      captures: sum((e) => e.metrics.outboundConversationsEngaged),
      sales,
      sent,
      responseRate: started > 0 ? answered / started : null,
      conversionRate: answered > 0 ? sales / answered : null,
      sentPerHour: hours > 0 ? sent / hours : null,
      receivedPerHour: hours > 0 ? received / hours : null,
    }
  }, [ranking])

  const kpiCards: { key: string; label: string; value: string; hint?: string; help: string }[] = [
    { key: 'conversas', label: 'Conversas iniciadas', value: String(totals.started), help: metricHelp.conversas },
    { key: 'resposta', label: 'Taxa de resposta', value: fmtPercent(totals.responseRate), help: metricHelp.resposta },
    { key: 'vendas', label: 'Vendas', value: String(totals.sales), help: metricHelp.vendas },
    { key: 'conversao', label: 'Conversão', value: fmtPercent(totals.conversionRate), help: metricHelp.conversao },
    { key: 'enviadas', label: 'Msgs enviadas', value: String(totals.sent), help: metricHelp.msgsEnviadas },
    { key: 'disparos', label: 'Disparos', value: String(totals.shots), help: metricHelp.disparos },
    { key: 'captacoes', label: 'Captações', value: String(totals.captures), help: metricHelp.captacoes },
    { key: 'naoresp', label: 'Não respondidas', value: String(totals.unanswered), help: metricHelp.naoRespondidas },
    { key: 'medenvh', label: 'Média env./h', value: fmtPerHour(totals.sentPerHour), help: metricHelp.mediaEnviadas },
    { key: 'medrech', label: 'Média rec./h', value: fmtPerHour(totals.receivedPerHour), help: metricHelp.mediaRecebidas },
    {
      key: 'espera',
      label: 'Espera de resposta',
      value: fmtMinutes(totals.waitAvg),
      hint:
        totals.waitAvg === null
          ? undefined
          : `mín ${fmtMinutes(totals.waitMin)} · máx ${fmtMinutes(totals.waitMax)}`,
      help: metricHelp.espera,
    },
  ]

  // Tipos de desfecho vêm do relatório (catálogo do servidor): cada um vira card,
  // coluna e opção de gráfico sem precisar de código novo aqui.
  const outcomeTypes = ranking?.[0]?.metrics.outcomes ?? []
  const availableMetrics = useMemo(() => chartMetricsFor(outcomeTypes), [outcomeTypes])

  const outcomeCards = outcomeTypes
    .filter((t) => t.typeCode !== 'sale')
    .map((t) => ({
      key: `outcome:${t.typeCode}`,
      label: t.name,
      value: String((ranking ?? []).reduce((s, e) => s + outcomeCount(e.metrics, t.typeCode), 0)),
      hint: undefined as string | undefined,
      help: outcomeHelp(t.name),
    }))

  const outcomeColumns = outcomeTypes
    .filter((t) => t.typeCode !== 'sale')
    .map((t) => ({
      key: `outcome:${t.typeCode}`,
      label: t.name,
      help: outcomeHelp(t.name),
      nowrap: false,
      render: (e: RankingEntryDto) => outcomeCount(e.metrics, t.typeCode),
    }))

  const allKpis = [...kpiCards, ...outcomeCards]
  const allColumns = [...tableColumns, ...outcomeColumns]
  const visibleKpis = allKpis.filter((c) => !hiddenKpis.includes(c.key))
  const visibleColumns = allColumns.filter((c) => !hiddenColumns.includes(c.key))

  function selectMetric(chartIndex: number, key: string) {
    // Métrica é exclusiva entre gráficos: escolher uma já usada troca as duas.
    setCharts((prev) => {
      const next = [...prev]
      const other = prev.indexOf(key)
      if (other !== -1 && other !== chartIndex) next[other] = prev[chartIndex]
      next[chartIndex] = key
      return next
    })
  }

  const [exportOpen, setExportOpen] = useState(false)

  function addChart() {
    setCharts((prev) => {
      const unused = availableMetrics.find((m) => !prev.includes(m.key))
      return unused ? [...prev, unused.key] : prev
    })
  }

  function removeChart(index: number) {
    setCharts((prev) => (prev.length > 1 ? prev.filter((_, i) => i !== index) : prev))
  }

  // As ações que no desktop são botões soltos no cabeçalho: no celular elas
  // viram uma folha, senão o topo da tela é uma parede de botões.
  const mobileActions: { label: string; onClick: () => void }[] = [
    { label: 'Exportar Excel', onClick: () => setExportOpen(true) },
    { label: 'Personalizar métricas', onClick: () => setKpisDialogOpen(true) },
    { label: 'Personalizar colunas', onClick: () => setColumnsDialogOpen(true) },
    { label: 'Organização dos gráficos', onClick: () => setLayoutDialogOpen(true) },
  ]

  return (
    <div className="space-y-6">
      {isMobile ? (
        <MobilePeriodBar
          title="Dashboard"
          period={period}
          onPeriodChange={setPeriod}
          lastUpdatedAt={dataUpdatedAt}
          isFetching={isFetching}
          onRefresh={refreshManually}
          pollMs={pollMs}
          onPollChange={setPollMs}
          actions={
            <Button variant="ghost" aria-label="Ações" onClick={() => setActionsOpen(true)}>
              ⋯
            </Button>
          }
        />
      ) : (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-xl font-bold">Dashboard</h2>
          <div className="flex flex-wrap items-center gap-1">
            <UpdateControls
              lastUpdatedAt={dataUpdatedAt}
              isFetching={isFetching}
              pollMs={pollMs}
              onPollChange={setPollMs}
              onRefresh={refreshManually}
            />
            {periodOptions.map((p) => (
              <Button
                key={p.value}
                variant={p.value === period ? 'primary' : 'ghost'}
                aria-pressed={p.value === period}
                onClick={() => setPeriod(p.value)}
              >
                {p.label}
              </Button>
            ))}
            <Button variant="ghost" onClick={() => setKpisDialogOpen(true)}>
              Personalizar
            </Button>
            <Button variant="ghost" onClick={() => setExportOpen(true)}>
              Exportar Excel
            </Button>
          </div>
        </div>
      )}

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar o ranking. A API está de pé?" />}

      {ranking && (
        <>
          {visibleKpis.length > 0 && (
            <div className="grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-5">
              {visibleKpis.map((c) => (
                <KpiCard
                  key={c.key}
                  data-testid={`kpi-${c.key}`}
                  label={c.label}
                  value={c.value}
                  hint={c.hint}
                  help={c.help}
                />
              ))}
            </div>
          )}

          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm font-semibold text-ink-muted">Gráficos</h3>
            <div className="flex items-center gap-1">
              <Button
                variant="ghost"
                onClick={addChart}
                disabled={charts.length >= availableMetrics.length}
              >
                + Adicionar gráfico
              </Button>
              {/* A organização em grade só muda alguma coisa a partir de `md`
                  (no celular é sempre uma coluna), então o botão sai daqui e
                  fica na folha de ações. */}
              {!isMobile && (
                <Button
                  variant="ghost"
                  aria-label="Organização dos gráficos"
                  title="Organização dos gráficos"
                  onClick={() => setLayoutDialogOpen(true)}
                  className="inline-flex items-center gap-1.5"
                >
                  <GridIcon />
                  Organização
                </Button>
              )}
            </div>
          </div>

          <div data-testid="charts-container" className={chartLayoutClass[chartLayout]}>
            {charts.map((metricKey, index) => (
              <RankingChartCard
                key={`${metricKey}-${index}`}
                index={index}
                metricKey={metricKey}
                ranking={ranking}
                availableMetrics={availableMetrics}
                canRemove={charts.length > 1}
                isMobile={isMobile}
                onSelect={(key) => selectMetric(index, key)}
                onRemove={() => removeChart(index)}
              />
            ))}
          </div>

          {/* Uma tabela de até 20 colunas em 360px só existe atrás de rolagem
              lateral. No celular cada vendedor vira um card com os índices em
              lista — exatamente as mesmas `visibleColumns` da tabela. */}
          {isMobile ? (
            <div data-testid="ranking-cards" className="space-y-3">
              <h3 className="text-sm font-semibold">Todos os índices</h3>
              {ranking.length === 0 ? (
                <EmptyState message="Nenhum vendedor com dados no período." />
              ) : (
                ranking.map((entry) => (
                  <ExpandableMetricCard
                    key={entry.sellerId}
                    data-testid={`ranking-card-${entry.sellerId}`}
                    title={
                      <Link
                        to={`/sellers/${entry.sellerId}`}
                        className="text-primary-strong hover:underline"
                      >
                        {entry.name}
                      </Link>
                    }
                    items={visibleColumns.map(
                      (c): MetricItem => ({
                        key: c.key,
                        label: c.label,
                        help: c.help,
                        value: c.render(entry),
                      }),
                    )}
                  />
                ))
              )}
            </div>
          ) : (
          <Card data-testid="ranking-table">
            <div className="mb-3 flex items-center justify-between gap-3">
              <h3 className="text-sm font-semibold">Todos os índices</h3>
              <Button variant="ghost" onClick={() => setColumnsDialogOpen(true)}>
                Personalizar colunas
              </Button>
            </div>
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-edge text-left text-xs text-ink-muted uppercase">
                    <Th label="Vendedor" />
                    {visibleColumns.map((c) => (
                      <Th key={c.key} label={c.label} help={c.help} />
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {ranking.map((entry) => (
                    <tr key={entry.sellerId} className="border-b border-edge last:border-0">
                      <td className="py-2 pr-4">
                        <Link
                          to={`/sellers/${entry.sellerId}`}
                          className="font-medium text-primary-strong hover:underline"
                        >
                          {entry.name}
                        </Link>
                      </td>
                      {visibleColumns.map((c) => (
                        <td key={c.key} className={c.nowrap ? 'py-2 pr-4 whitespace-nowrap' : 'py-2 pr-4'}>
                          {c.render(entry)}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
          )}
        </>
      )}

      <Dialog open={actionsOpen} onClose={() => setActionsOpen(false)} title="Ações">
        <div data-testid="dashboard-actions" className="flex flex-col gap-2">
          {mobileActions.map((action) => (
            <Button
              key={action.label}
              variant="ghost"
              className="w-full justify-start"
              onClick={() => {
                setActionsOpen(false)
                action.onClick()
              }}
            >
              {action.label}
            </Button>
          ))}
        </div>
      </Dialog>

      <Dialog open={kpisDialogOpen} onClose={() => setKpisDialogOpen(false)} title="Métricas globais">
        <div data-testid="customize-kpis" className="max-h-[55dvh] overflow-y-auto pr-1 md:max-h-[65vh]">
          <VisibilityChecklist
            items={allKpis}
            hidden={hiddenKpis}
            onToggle={(key) => setHiddenKpis(toggleHidden(hiddenKpis, key))}
          />
        </div>
      </Dialog>

      <Dialog
        open={columnsDialogOpen}
        onClose={() => setColumnsDialogOpen(false)}
        title="Colunas da lista de funcionários"
      >
        <div data-testid="customize-columns" className="max-h-[55dvh] overflow-y-auto pr-1 md:max-h-[65vh]">
          <VisibilityChecklist
            items={allColumns}
            hidden={hiddenColumns}
            onToggle={(key) => setHiddenColumns(toggleHidden(hiddenColumns, key))}
          />
        </div>
      </Dialog>

      <Dialog
        open={layoutDialogOpen}
        onClose={() => setLayoutDialogOpen(false)}
        title="Organização dos gráficos"
      >
        <div data-testid="customize-layout" className="flex flex-col gap-1.5">
          {chartLayoutOptions.map((o) => (
            <label key={o.value} className="flex items-center gap-2 text-sm">
              <input
                type="radio"
                name="chart-layout"
                className="accent-primary"
                checked={chartLayout === o.value}
                onChange={() => setChartLayout(o.value)}
              />
              {o.label}
            </label>
          ))}
        </div>
      </Dialog>

      <ExportReportDialog open={exportOpen} onClose={() => setExportOpen(false)} range={range} />
    </div>
  )
}
