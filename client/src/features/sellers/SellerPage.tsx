import { useMemo, useRef } from 'react'
import { Link, useParams } from 'react-router-dom'
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { useSellerReport } from '../../api/queries'
import { KpiCard } from '../../components/KpiCard'
import { Button, Card, EmptyState, ErrorState, Spinner, StatusBadge } from '../../components/ui'
import { fmtDateTime, fmtHours, fmtMinutes, fmtPercent, fmtPerHour, periodOptions } from '../../lib/format'
import { chartInk, chartPalette } from '../../lib/palette'
import { metricHelp } from '../../lib/metrics'
import { usePeriodRange } from '../../lib/usePeriodRange'
import { usePollMs } from '../../lib/polling'
import { UpdateControls } from '../../components/UpdateControls'
import { MobilePeriodBar } from '../../components/mobile/PeriodBar'
import { useIsMobile } from '../../lib/useIsMobile'

export function SellerPage() {
  const { id = '' } = useParams()
  const isMobile = useIsMobile()
  const [pollMs, setPollMs] = usePollMs()
  const { period, setPeriod, range, refreshNow } = usePeriodRange(pollMs)
  const freshRef = useRef(false)
  const {
    data: report,
    isLoading,
    isError,
    isFetching,
    dataUpdatedAt,
    refetch,
  } = useSellerReport(id, range, pollMs, freshRef)

  function refreshManually() {
    freshRef.current = true
    refreshNow()
    void refetch()
  }

  const chartData = useMemo(
    () =>
      (report?.numbers ?? []).map((n) => ({
        name: n.phone,
        Enviadas: n.metrics.messagesSent,
        Recebidas: n.metrics.messagesReceived,
      })),
    [report],
  )

  return (
    <div className="space-y-6">
      {isMobile ? (
        <MobilePeriodBar
          title={report?.name ?? 'Vendedor'}
          above={
            <Link to="/" className="text-xs text-ink-muted hover:underline">
              ← Dashboard
            </Link>
          }
          period={period}
          onPeriodChange={setPeriod}
          lastUpdatedAt={dataUpdatedAt}
          isFetching={isFetching}
          onRefresh={refreshManually}
          pollMs={pollMs}
          onPollChange={setPollMs}
        />
      ) : (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <Link to="/" className="text-xs text-ink-muted hover:underline">
              ← Dashboard
            </Link>
            <h2 className="text-xl font-bold">{report?.name ?? 'Vendedor'}</h2>
          </div>
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
          </div>
        </div>
      )}

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar o relatório do vendedor." />}

      {report && (
        <>
          <div className="grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-6">
            <KpiCard label="Conversas" value={String(report.totals.conversationsStarted)} help={metricHelp.conversas} />
            <KpiCard label="Taxa de resposta" value={fmtPercent(report.totals.responseRate)} help={metricHelp.resposta} />
            <KpiCard label="1ª resposta (mediana)" value={fmtMinutes(report.totals.medianFirstResponseMinutes)} help={metricHelp.primeiraResposta} />
            <KpiCard label="Vendas" value={String(report.totals.sales)} help={metricHelp.vendas} />
            <KpiCard label="Conversão" value={fmtPercent(report.totals.conversionRate)} help={metricHelp.conversao} />
            <KpiCard label="Fechamento (média)" value={fmtHours(report.totals.avgTimeToCloseBusinessHours)} help={metricHelp.fechamento} />
            <KpiCard label="Disparos" value={String(report.totals.outboundConversationsStarted)} help={metricHelp.disparos} />
            <KpiCard label="Captações" value={String(report.totals.outboundConversationsEngaged)} help={metricHelp.captacoes} />
            <KpiCard label="Não respondidas" value={String(report.totals.conversationsUnanswered)} help={metricHelp.naoRespondidas} />
            <KpiCard label="Média env./h" value={fmtPerHour(report.totals.avgSentPerBusinessHour)} help={metricHelp.mediaEnviadas} />
            <KpiCard label="Média rec./h" value={fmtPerHour(report.totals.avgReceivedPerBusinessHour)} help={metricHelp.mediaRecebidas} />
            <KpiCard label="Último envio" value={fmtDateTime(report.totals.lastOutboundMessageAt)} help={metricHelp.ultimoEnvio} />
            <KpiCard
              label="Espera de resposta"
              value={fmtMinutes(report.totals.avgResponseMinutes)}
              hint={
                report.totals.avgResponseMinutes === null
                  ? undefined
                  : `mín ${fmtMinutes(report.totals.minResponseMinutes)} · máx ${fmtMinutes(report.totals.maxResponseMinutes)}`
              }
              help={metricHelp.espera}
            />
          </div>

          <Card>
            <h3 className="mb-4 text-sm font-semibold">Mensagens por número</h3>
            {chartData.length === 0 ? (
              <EmptyState message="Este vendedor ainda não tem números cadastrados." />
            ) : (
              <ResponsiveContainer width="100%" height={Math.max(140, chartData.length * 56)}>
                <BarChart
                  data={chartData}
                  layout="vertical"
                  margin={isMobile ? { left: 0, right: 12 } : { left: 8, right: 24 }}
                >
                  <CartesianGrid horizontal={false} stroke={chartInk.grid} />
                  <XAxis
                    type="number"
                    tick={{ fill: chartInk.axis, fontSize: isMobile ? 10 : 12 }}
                    axisLine={false}
                    tickLine={false}
                  />
                  {/* 130px de eixo num gráfico de ~300px é quase metade da
                      largura: no celular o número do vendedor encolhe. */}
                  <YAxis
                    type="category"
                    dataKey="name"
                    width={isMobile ? 92 : 130}
                    tick={{ fill: chartInk.axis, fontSize: isMobile ? 10 : 12 }}
                    axisLine={false}
                    tickLine={false}
                  />
                  <Tooltip contentStyle={{ background: chartInk.tooltipBg, borderRadius: 8 }} />
                  <Legend wrapperStyle={{ fontSize: 12, color: chartInk.axis }} />
                  <Bar dataKey="Enviadas" fill={chartPalette[0]} barSize={12} radius={[0, 4, 4, 0]} />
                  <Bar dataKey="Recebidas" fill={chartPalette[1]} barSize={12} radius={[0, 4, 4, 0]} />
                </BarChart>
              </ResponsiveContainer>
            )}
          </Card>

          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {report.numbers.map((n) => (
              <Card key={n.numberId}>
                <div className="mb-3 flex items-center justify-between">
                  <p className="font-semibold">{n.phone}</p>
                  <StatusBadge status={n.status} />
                </div>
                <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
                  <dt className="text-ink-muted">Uptime</dt>
                  <dd className="text-right">{n.metrics.uptimePercent.toFixed(0)}%</dd>
                  <dt className="text-ink-muted">Bans</dt>
                  <dd className="text-right">{n.metrics.banCount}</dd>
                  <dt className="text-ink-muted">Conversas</dt>
                  <dd className="text-right">{n.metrics.conversationsStarted}</dd>
                  <dt className="text-ink-muted">Taxa de leitura</dt>
                  <dd className="text-right">{fmtPercent(n.metrics.readRate)}</dd>
                </dl>
              </Card>
            ))}
          </div>

          <p className="text-xs text-ink-muted">
            Ações de número (novo QR, ban permanente) ficam em{' '}
            <Link to="/registry" className="text-primary-strong hover:underline">
              Cadastros
            </Link>
            .
          </p>
        </>
      )}
    </div>
  )
}
