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
        // O rótulo é sempre curto e não encolhe; quem quebra linha é o valor.
        // Com `shrink-0` no valor, um resumo de uma frase (a leitura da IA)
        // empurrava o card e a página inteira ganhava rolagem lateral.
        <div key={item.key} className="flex items-start justify-between gap-3 py-2">
          <dt className="flex shrink-0 items-center gap-1.5 text-ink-muted">
            {item.label}
            {item.help && <InfoTip text={item.help} />}
          </dt>
          <dd className="min-w-0 text-right font-medium break-words text-ink">{item.value}</dd>
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
  details,
  previewCount = 4,
  moreLabel = 'ver mais índices',
  ...rest
}: {
  title: ReactNode
  items: MetricItem[]
  // Conteúdo livre que só aparece expandido — o detalhe da análise da IA é
  // texto corrido (evidência, objeções, mensagem sugerida), não rótulo→valor.
  details?: ReactNode
  previewCount?: number
  moreLabel?: string
} & { 'data-testid'?: string }) {
  const [expanded, setExpanded] = useState(false)
  const hasMore = items.length > previewCount || details !== undefined
  const visible = expanded || items.length <= previewCount ? items : items.slice(0, previewCount)

  return (
    <Card {...rest}>
      <div className="mb-1 font-semibold">{title}</div>
      <MetricList items={visible} />
      {expanded && details}
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
