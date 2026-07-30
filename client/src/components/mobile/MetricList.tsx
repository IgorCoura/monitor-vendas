import { useState, type ReactNode } from 'react'
import { Card, InfoTip } from '../ui'

export type MetricItem = { key: string; label: string; value: ReactNode; help?: string }

// Substituto das tabelas no celular: rótulo à esquerda, valor à direita. Uma
// tabela de 20 colunas em 360px só existe atrás de rolagem lateral; aqui cada
// índice fica legível e mantém o "?" com a explicação da métrica.
export function MetricList({ items }: { items: MetricItem[] }) {
  return (
    <dl className="divide-y divide-edge/60 text-sm">
      {items.map((item) => (
        <div key={item.key} className="flex items-start justify-between gap-3 py-2">
          <dt className="flex min-w-0 items-center gap-1.5 text-ink-muted">
            <span className="min-w-0">{item.label}</span>
            {item.help && <InfoTip text={item.help} />}
          </dt>
          <dd className="shrink-0 text-right font-medium text-ink">{item.value}</dd>
        </div>
      ))}
    </dl>
  )
}

// Card de uma linha da antiga tabela: mostra os primeiros índices e esconde o
// resto atrás de "ver mais" — com 20 colunas, abrir tudo de uma vez daria uma
// rolagem interminável.
export function ExpandableMetricCard({
  title,
  items,
  previewCount = 4,
  moreLabel = 'ver mais índices',
  ...rest
}: {
  title: ReactNode
  items: MetricItem[]
  previewCount?: number
  moreLabel?: string
} & { 'data-testid'?: string }) {
  const [expanded, setExpanded] = useState(false)
  const hasMore = items.length > previewCount
  const visible = expanded || !hasMore ? items : items.slice(0, previewCount)

  return (
    <Card {...rest}>
      <div className="mb-1 font-semibold">{title}</div>
      <MetricList items={visible} />
      {hasMore && (
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          aria-expanded={expanded}
          className="mt-1 flex min-h-11 w-full items-center justify-center gap-1 text-sm font-medium text-primary-strong"
        >
          {expanded ? 'ver menos' : moreLabel}
          <span aria-hidden="true">{expanded ? '▴' : '▾'}</span>
        </button>
      )}
    </Card>
  )
}
