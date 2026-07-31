import { useMemo, useState } from 'react'
import clsx from 'clsx'
import { api } from '../../api/client'
import { useExportMetrics, useSellers } from '../../api/queries'
import type { DateRange } from '../../api/client'
import type { ReportExportFilters } from '../../api/types'
import { Button, Dialog } from '../../components/ui'
import { useIsMobile } from '../../lib/useIsMobile'

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
// gráficos e vendedores todos abertos, o botão de baixar ficava dezenas de chips
// abaixo — e o usuário rolava sem saber que ele existia.
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

  const filters: ReportExportFilters = useMemo(
    () => ({
      from: range.from,
      to: range.to,
      sellerIds,
      metrics,
      charts,
      includeNumbers,
    }),
    [range.from, range.to, sellerIds, metrics, charts, includeNumbers],
  )

  function toggle(list: string[], set: (value: string[]) => void, key: string) {
    set(list.includes(key) ? list.filter((k) => k !== key) : [...list, key])
  }

  // Métricas escolhidas mandam nos gráficos disponíveis: gráfico de coluna que
  // não está na planilha não teria de onde ler.
  const chartCandidates = (metricOptions ?? []).filter(
    (option) => metrics.length === 0 || metrics.includes(option.key),
  )

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Exportar relatório em Excel"
      size="lg"
      footer={
        <div data-testid="export-actions" className="flex justify-end gap-2 max-md:flex-col-reverse">
          <Button variant="ghost" onClick={onClose}>
            Cancelar
          </Button>
          {/* A planilha sai das métricas já calculadas e leva milissegundos: o
              download é do navegador, e o nome do arquivo vem do servidor. */}
          <a
            href={api.reports.exportUrl(filters)}
            data-testid="export-download"
            onClick={onClose}
            className="flex min-h-11 items-center justify-center rounded-lg bg-primary px-3 py-1.5 text-sm font-medium text-white hover:bg-primary-strong md:min-h-0"
          >
            Baixar planilha
          </a>
        </div>
      }
    >
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

        {/* A leitura por IA saiu daqui: esta planilha é só fato medido, e por
            isso nunca custa nada. */}
        <p className="text-xs text-ink-muted">
          A leitura das conversas por IA tem planilha própria, na tela “Análises por IA”.
        </p>
      </div>
    </Dialog>
  )
}
