import { useEffect, useMemo, useState } from 'react'
import clsx from 'clsx'
import { api, ApiError } from '../../api/client'
import {
  useAiBudget,
  useCreateExport,
  useEstimateExport,
  useExportMetrics,
  useExportStatus,
  useSellers,
} from '../../api/queries'
import type { DateRange } from '../../api/client'
import type { ReportExportFilters } from '../../api/types'
import { Button, Dialog, ErrorState, Spinner } from '../../components/ui'
import { useIsMobile } from '../../lib/useIsMobile'

function brl(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2 })
}

function Chip({
  selected,
  onClick,
  children,
}: {
  selected: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onClick}
      className={clsx(
        'min-h-11 rounded-full border px-3 py-1 text-xs font-medium transition-colors md:min-h-0',
        selected
          ? 'border-primary bg-primary-soft text-primary-strong'
          : 'border-edge bg-card text-ink-muted hover:bg-surface',
      )}
    >
      {children}
    </button>
  )
}

// No celular cada bloco de chips vira uma seção que abre e fecha: com métricas,
// gráficos e vendedores todos abertos, o botão "Gerar planilha" ficava dezenas
// de chips abaixo — e o usuário rolava sem saber que ele existia.
function ChipSection({
  title,
  hint,
  selectedCount,
  collapsible,
  testId,
  children,
}: {
  title: string
  hint?: string
  selectedCount: number
  collapsible: boolean
  testId?: string
  children: React.ReactNode
}) {
  const [open, setOpen] = useState(false)

  if (!collapsible) {
    return (
      <div>
        <p className="text-xs font-medium text-ink-muted">
          {title} {hint}
        </p>
        <div className="mt-2 flex flex-wrap gap-2" data-testid={testId}>
          {children}
        </div>
      </div>
    )
  }

  return (
    <div className="rounded-lg border border-edge">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className="flex min-h-11 w-full items-center justify-between gap-2 px-3 text-left text-sm font-medium"
      >
        <span>
          {title}
          {selectedCount > 0 && (
            <span className="ml-1.5 text-xs text-primary-strong">({selectedCount})</span>
          )}
        </span>
        <span aria-hidden="true" className="text-ink-muted">
          {open ? '▴' : '▾'}
        </span>
      </button>
      {open && (
        <div className="border-t border-edge px-3 py-3">
          {hint && <p className="mb-2 text-xs text-ink-muted">{hint}</p>}
          <div className="flex flex-wrap gap-2" data-testid={testId}>
            {children}
          </div>
        </div>
      )}
    </div>
  )
}

export function ExportReportDialog({
  open,
  onClose,
  range,
}: {
  open: boolean
  onClose: () => void
  range: DateRange
}) {
  const isMobile = useIsMobile()
  const { data: metricOptions } = useExportMetrics()
  const { data: sellers } = useSellers()
  const [metrics, setMetrics] = useState<string[]>([])
  const [charts, setCharts] = useState<string[]>([])
  const [sellerIds, setSellerIds] = useState<string[]>([])
  const [includeNumbers, setIncludeNumbers] = useState(true)
  const [includeAi, setIncludeAi] = useState(false)
  const [includeAudio, setIncludeAudio] = useState(false)
  const [exportId, setExportId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const { data: budget } = useAiBudget(open && includeAi)
  const estimate = useEstimateExport()
  const createExport = useCreateExport()
  const { data: job } = useExportStatus(exportId)

  const filters: ReportExportFilters = useMemo(
    () => ({
      from: range.from,
      to: range.to,
      sellerIds,
      metrics,
      charts,
      includeNumbers,
      includeAi,
      includeAudio: includeAi && includeAudio,
    }),
    [range.from, range.to, sellerIds, metrics, charts, includeNumbers, includeAi, includeAudio],
  )

  // Só estima com IA ligada: sem ela não há gasto e a chamada seria ruído.
  const { mutate: runEstimate, reset: resetEstimate } = estimate
  useEffect(() => {
    if (!open || !includeAi) {
      resetEstimate()
      return
    }

    runEstimate(filters)
  }, [open, includeAi, filters, runEstimate, resetEstimate])

  function toggle(list: string[], set: (value: string[]) => void, key: string) {
    set(list.includes(key) ? list.filter((k) => k !== key) : [...list, key])
  }

  function close() {
    setExportId(null)
    setError(null)
    onClose()
  }

  async function submit() {
    setError(null)
    try {
      const created = await createExport.mutateAsync(filters)
      setExportId(created.id)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao pedir o relatório.')
    }
  }

  // Métricas escolhidas mandam nos gráficos disponíveis: gráfico de coluna que
  // não está na planilha não teria de onde ler.
  const chartCandidates = (metricOptions ?? []).filter(
    (option) => metrics.length === 0 || metrics.includes(option.key),
  )
  const cost = estimate.data
  const blocked = includeAi && cost !== undefined && !cost.affordable

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Exportar relatório em Excel"
      size="lg"
      footer={
        job ? undefined : (
          <div
            data-testid="export-actions"
            className="flex justify-end gap-2 max-md:flex-col-reverse"
          >
            <Button variant="ghost" onClick={close}>
              Cancelar
            </Button>
            <Button onClick={submit} disabled={createExport.isPending || blocked}>
              Gerar planilha
            </Button>
          </div>
        )
      }
    >
      {job ? (
        <div className="space-y-3" data-testid="export-progress">
          {(job.status === 'Pending' || job.status === 'Running') && (
            <>
              <Spinner />
              <p className="text-sm">
                {job.phase ?? 'Gerando a planilha'}…
                {job.phase === 'Analisando conversas' && job.totalConversations > 0 &&
                  ` ${job.analyzedConversations + job.cachedConversations}/${job.totalConversations}`}
              </p>
              {job.phase === 'Sintetizando vendedores' && (
                // Esta fase espera cota do provedor: sem avisar, a tela parece travada.
                <p className="text-xs text-ink-muted">
                  Pode levar alguns minutos por causa do limite de chamadas da IA. Passado o prazo,
                  a planilha sai assim mesmo e a síntese fica marcada como pendente.
                </p>
              )}
            </>
          )}

          {job.status === 'Completed' && (
            <>
              <p className="text-sm">Planilha pronta.</p>
              {job.totalConversations > 0 && (
                <p className="text-xs text-ink-muted">
                  {job.analyzedConversations} analisadas agora · {job.cachedConversations} reaproveitadas ·{' '}
                  {job.skippedConversations} sem análise · custo {brl(job.costBrl)}
                </p>
              )}
              <a
                href={api.reports.exportFileUrl(job.id)}
                data-testid="export-download"
                className="flex min-h-11 items-center justify-center rounded-lg bg-primary px-3 py-1.5 text-sm font-medium text-white hover:bg-primary-strong md:inline-flex md:min-h-0"
              >
                Baixar planilha
              </a>
            </>
          )}

          {job.status === 'Failed' && <ErrorState message={job.error ?? 'A exportação falhou.'} />}

          <Button variant="ghost" onClick={close}>
            Fechar
          </Button>
        </div>
      ) : (
        <div className="space-y-4">
          <ChipSection
            title="Métricas"
            hint={metrics.length === 0 ? '(nenhuma marcada = todas)' : undefined}
            selectedCount={metrics.length}
            collapsible={isMobile}
            testId="metric-chips"
          >
            {(metricOptions ?? []).map((option) => (
              <Chip
                key={option.key}
                selected={metrics.includes(option.key)}
                onClick={() => toggle(metrics, setMetrics, option.key)}
              >
                {option.label}
              </Chip>
            ))}
          </ChipSection>

          <ChipSection
            title="Gráficos"
            hint="(barras por vendedor)"
            selectedCount={charts.length}
            collapsible={isMobile}
            testId="chart-chips"
          >
            {chartCandidates.map((option) => (
              <Chip
                key={option.key}
                selected={charts.includes(option.key)}
                onClick={() => toggle(charts, setCharts, option.key)}
              >
                {option.label}
              </Chip>
            ))}
          </ChipSection>

          {(sellers ?? []).length > 0 && (
            <ChipSection
              title="Vendedores"
              hint={sellerIds.length === 0 ? '(nenhum marcado = todos)' : undefined}
              selectedCount={sellerIds.length}
              collapsible={isMobile}
            >
              {(sellers ?? []).map((seller) => (
                <Chip
                  key={seller.id}
                  selected={sellerIds.includes(seller.id)}
                  onClick={() => toggle(sellerIds, setSellerIds, seller.id)}
                >
                  {seller.name}
                </Chip>
              ))}
            </ChipSection>
          )}

          <label className="flex min-h-11 items-center gap-2 text-sm md:min-h-0">
            <input
              type="checkbox"
              className="h-4 w-4"
              checked={includeNumbers}
              onChange={(e) => setIncludeNumbers(e.target.checked)}
            />
            Incluir aba por número
          </label>

          <label className="flex min-h-11 items-center gap-2 text-sm md:min-h-0">
            <input
              type="checkbox"
              className="h-4 w-4"
              checked={includeAi}
              onChange={(e) => setIncludeAi(e.target.checked)}
              aria-label="Incluir análise por IA"
            />
            Incluir análise por IA
          </label>

          {includeAi && (
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                className="mt-1"
                checked={includeAudio}
                onChange={(e) => setIncludeAudio(e.target.checked)}
                aria-label="Enviar áudios das conversas"
              />
              <span>
                Enviar os áudios das conversas
                <span className="block text-xs text-ink-muted">
                  A IA passa a ouvir os áudios em vez de só saber que existiram. Atenção: a{' '}
                  <strong>voz do cliente vai para o provedor de IA</strong> — o mascaramento de nome e
                  telefone não alcança áudio. Custa bem mais que texto (cerca de 32 tokens por segundo).
                </span>
              </span>
            </label>
          )}

          {includeAi && (
            <div className="rounded-lg bg-surface p-3 text-xs" data-testid="ai-estimate">
              {estimate.isPending && <span className="text-ink-muted">Calculando o custo…</span>}
              {cost && (
                <>
                  <p className="text-ink">
                    Custo estimado {brl(cost.estimatedBrl)} · saldo {brl(cost.available)}
                    {budget && !budget.enabled && ' (controle de saldo desligado)'}
                  </p>
                  <p className="mt-1 text-ink-muted">
                    {cost.conversations} conversas no período · {cost.cached} já analisadas (não custam nada)
                  </p>
                  {cost.truncated && (
                    <p className="mt-1 text-ink-muted">
                      O período tem mais conversas que o limite por exportação; as mais recentes entram primeiro.
                    </p>
                  )}
                  {blocked && (
                    <p className="mt-1 font-medium text-danger">
                      O saldo da janela não cobre esta análise. Reduza o período ou espere a recarga.
                    </p>
                  )}
                  <p className="mt-2 text-ink-muted">
                    As conversas do período são enviadas ao provedor de IA com nome e telefone do cliente
                    mascarados.
                  </p>
                </>
              )}
            </div>
          )}

          {error && <ErrorState message={error} />}
        </div>
      )}
    </Dialog>
  )
}
