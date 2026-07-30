import type { HTMLAttributes } from 'react'
import { Card, InfoTip } from './ui'

export function KpiCard({
  label,
  value,
  hint,
  help,
  ...rest
}: {
  label: string
  value: string
  hint?: string
  help?: string
} & HTMLAttributes<HTMLDivElement>) {
  return (
    <Card className="min-w-0" {...rest}>
      <p className="flex items-center gap-1.5 text-xs font-medium tracking-wide text-ink-muted uppercase">
        <span className="truncate">{label}</span>
        {help && <InfoTip text={help} />}
      </p>
      <p className="mt-1 text-2xl font-bold text-ink">{value}</p>
      {hint && <p className="mt-0.5 text-xs text-ink-muted">{hint}</p>}
    </Card>
  )
}
