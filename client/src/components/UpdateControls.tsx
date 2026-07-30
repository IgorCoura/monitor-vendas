import clsx from 'clsx'
import { Button } from './ui'
import { fmtDateTime } from '../lib/format'
import { pollOptions, type PollMs } from '../lib/polling'

function RefreshIcon({ spinning }: { spinning: boolean }) {
  return (
    <svg
      viewBox="0 0 16 16"
      aria-hidden="true"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      className={clsx('h-3.5 w-3.5', spinning && 'animate-spin')}
    >
      <path d="M13.5 8a5.5 5.5 0 1 1-1.9-4.16" />
      <path d="M13.9 1.6v2.9h-2.9" />
    </svg>
  )
}

// Última atualização + atualização manual + escolha do intervalo automático.
// Fica à esquerda dos botões de período.
export function UpdateControls({
  lastUpdatedAt,
  isFetching,
  pollMs,
  onPollChange,
  onRefresh,
}: {
  lastUpdatedAt: number
  isFetching: boolean
  pollMs: PollMs
  onPollChange: (value: PollMs) => void
  onRefresh: () => void
}) {
  return (
    <div className="mr-1 flex flex-wrap items-center gap-1 border-edge pr-2 sm:border-r">
      <span className="mr-1 text-xs text-ink-muted" data-testid="last-poll">
        Atualizado {lastUpdatedAt > 0 ? fmtDateTime(new Date(lastUpdatedAt).toISOString()) : '—'}
      </span>
      <Button
        variant="ghost"
        aria-label="Atualizar agora"
        title="Atualizar agora"
        onClick={onRefresh}
        disabled={isFetching}
        className="inline-flex items-center"
      >
        <RefreshIcon spinning={isFetching} />
      </Button>
      {pollOptions.map((o) => (
        <Button
          key={o.label}
          variant={o.value === pollMs ? 'primary' : 'ghost'}
          aria-pressed={o.value === pollMs}
          aria-label={o.help}
          title={o.help}
          onClick={() => onPollChange(o.value)}
        >
          {o.label}
        </Button>
      ))}
    </div>
  )
}
