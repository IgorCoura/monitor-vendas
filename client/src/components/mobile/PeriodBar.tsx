import clsx from 'clsx'
import type { ReactNode } from 'react'
import { Button, Select } from '../ui'
import { RefreshIcon } from '../UpdateControls'
import { fmtDateTime, periodOptions, type Period } from '../../lib/format'
import { pollOptions, type PollMs } from '../../lib/polling'

// Cabeçalho das telas de relatório no celular. No desktop os mesmos controles
// são uma fileira de ~12 botões que, em 360px, viravam quatro linhas de botão
// antes de qualquer dado aparecer: aqui o período é um seletor segmentado de
// largura cheia, o intervalo de atualização virou <select> nativo e o resto das
// ações fica atrás do botão passado em `actions`.
export function MobilePeriodBar({
  title,
  above,
  period,
  onPeriodChange,
  lastUpdatedAt,
  isFetching,
  onRefresh,
  pollMs,
  onPollChange,
  actions,
}: {
  title: ReactNode
  above?: ReactNode
  period: Period
  onPeriodChange: (value: Period) => void
  lastUpdatedAt: number
  isFetching: boolean
  onRefresh: () => void
  pollMs: PollMs
  onPollChange: (value: PollMs) => void
  actions?: ReactNode
}) {
  return (
    <div className="space-y-2">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          {above}
          <h2 className="truncate text-xl font-bold">{title}</h2>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <Button
            variant="ghost"
            aria-label="Atualizar agora"
            onClick={onRefresh}
            disabled={isFetching}
          >
            <RefreshIcon spinning={isFetching} />
          </Button>
          {actions}
        </div>
      </div>

      <div
        role="group"
        aria-label="Período"
        className="flex gap-1 rounded-lg border border-edge bg-card p-1"
      >
        {periodOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            aria-pressed={option.value === period}
            onClick={() => onPeriodChange(option.value)}
            className={clsx(
              'min-h-10 flex-1 rounded-md text-sm font-medium transition-colors',
              option.value === period
                ? 'bg-primary text-white'
                : 'text-ink-muted hover:bg-surface',
            )}
          >
            {option.label}
          </button>
        ))}
      </div>

      <div className="flex items-center justify-between gap-2 text-xs text-ink-muted">
        <span data-testid="last-poll">
          Atualizado{' '}
          {lastUpdatedAt > 0 ? fmtDateTime(new Date(lastUpdatedAt).toISOString()) : '—'}
        </span>
        <label className="flex shrink-0 items-center gap-1.5">
          Atualizar
          <Select
            aria-label="Intervalo de atualização"
            value={String(pollMs)}
            onChange={(e) =>
              onPollChange(e.target.value === 'null' ? null : Number(e.target.value))
            }
            className="py-1"
          >
            {pollOptions.map((option) => (
              <option key={option.label} value={String(option.value)}>
                {option.label}
              </option>
            ))}
          </Select>
        </label>
      </div>
    </div>
  )
}
