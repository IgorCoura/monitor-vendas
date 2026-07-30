import { useEffect, useMemo, useState } from 'react'
import clsx from 'clsx'
import { api } from '../../api/client'
import { useContacts, useOutcomeTypes, useSellers } from '../../api/queries'
import type { ContactFilters } from '../../api/types'
import { Button, Card, EmptyState, ErrorState, Input, Spinner } from '../../components/ui'
import { dayEndIso, dayStartIso, fmtDateTime, toDayInput } from '../../lib/format'
import { ShareDialog } from './ShareDialog'

const PAGE_SIZE = 50
const NO_OUTCOME = 'none'

const bannedOptions = [
  { value: 'all', label: 'Todos' },
  { value: 'banned', label: 'Número banido' },
  { value: 'active', label: 'Número ativo' },
] as const

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
        'rounded-full border px-3 py-1 text-xs font-medium transition-colors',
        selected
          ? 'border-primary bg-primary-soft text-primary-strong'
          : 'border-edge bg-card text-ink-muted hover:bg-surface',
      )}
    >
      {children}
    </button>
  )
}

export function ContactsPage() {
  const now = useMemo(() => new Date(), [])
  const [from, setFrom] = useState(() => toDayInput(new Date(now.getTime() - 30 * 86_400_000)))
  const [to, setTo] = useState(() => toDayInput(now))
  const [sellerId, setSellerId] = useState('')
  const [outcomeTypes, setOutcomeTypes] = useState<string[]>([])
  const [banned, setBanned] = useState<ContactFilters['banned']>('all')
  const [page, setPage] = useState(1)
  const [shareOpen, setShareOpen] = useState(false)

  const filters: ContactFilters = useMemo(
    () => ({ from: dayStartIso(from), to: dayEndIso(to), sellerId, outcomeTypes, banned }),
    [from, to, sellerId, outcomeTypes, banned],
  )

  useEffect(() => setPage(1), [filters])

  const { data, isLoading, isError } = useContacts(filters, page, PAGE_SIZE)
  const { data: sellers } = useSellers()
  const { data: types } = useOutcomeTypes()

  const total = data?.total ?? 0
  const first = total === 0 ? 0 : (page - 1) * PAGE_SIZE + 1
  const last = Math.min(page * PAGE_SIZE, total)

  function toggleOutcome(code: string) {
    setOutcomeTypes((current) =>
      current.includes(code) ? current.filter((c) => c !== code) : [...current, code],
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-xl font-bold">Contatos</h2>
          <p className="text-sm text-ink-muted">
            Uma linha por cliente. Vendedor, número e banimento são os da{' '}
            <strong>conversa mais recente dentro do período</strong>; as datas e as etiquetas
            também consideram só o período filtrado.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="ghost" onClick={() => setShareOpen(true)} disabled={total === 0}>
            Enviar por WhatsApp
          </Button>
          <a
            href={api.contacts.exportUrl(filters)}
            data-testid="export-link"
            className="rounded-lg bg-primary px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-primary-strong"
          >
            Exportar Excel{total > 0 ? ` (${total})` : ''}
          </a>
        </div>
      </div>

      <Card data-testid="filters" className="space-y-4">
        <div className="flex flex-wrap items-end gap-4">
          <label className="flex flex-col gap-1 text-xs font-medium text-ink-muted">
            De
            <Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="De" />
          </label>
          <label className="flex flex-col gap-1 text-xs font-medium text-ink-muted">
            Até
            <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} aria-label="Até" />
          </label>
          <label className="flex flex-col gap-1 text-xs font-medium text-ink-muted">
            Vendedor
            <select
              aria-label="Vendedor"
              value={sellerId}
              onChange={(e) => setSellerId(e.target.value)}
              className="rounded-lg border border-edge bg-card px-3 py-1.5 text-sm text-ink focus:border-primary focus:outline-none"
            >
              <option value="">Todos</option>
              {(sellers ?? []).map((seller) => (
                <option key={seller.id} value={seller.id}>
                  {seller.name}
                </option>
              ))}
            </select>
          </label>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs font-medium text-ink-muted">Desfecho:</span>
          {(types ?? []).map((type) => (
            <Chip
              key={type.code}
              selected={outcomeTypes.includes(type.code)}
              onClick={() => toggleOutcome(type.code)}
            >
              {type.name}
            </Chip>
          ))}
          <Chip
            selected={outcomeTypes.includes(NO_OUTCOME)}
            onClick={() => toggleOutcome(NO_OUTCOME)}
          >
            Sem desfecho
          </Chip>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs font-medium text-ink-muted">Banimento:</span>
          {bannedOptions.map((option) => (
            <Chip
              key={option.value}
              selected={banned === option.value}
              onClick={() => setBanned(option.value)}
            >
              {option.label}
            </Chip>
          ))}
        </div>
      </Card>

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar os contatos." />}

      {data && data.items.length === 0 && <EmptyState message="Nenhum contato com os filtros atuais." />}

      {data && data.items.length > 0 && (
        <Card className="overflow-x-auto p-0">
          <table data-testid="contacts-table" className="w-full text-sm">
            <thead>
              <tr className="border-b border-edge text-left text-xs uppercase tracking-wide text-ink-muted">
                <th className="px-4 py-3">Cliente</th>
                <th className="px-4 py-3">Número</th>
                <th className="px-4 py-3">1ª mensagem</th>
                <th className="px-4 py-3">Última mensagem</th>
                <th className="px-4 py-3">Desfecho</th>
                <th className="px-4 py-3">Etiquetas</th>
                <th className="px-4 py-3">Vendedor</th>
                <th className="px-4 py-3">Nº do vendedor</th>
                <th className="px-4 py-3">Banido?</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((row) => (
                <tr key={row.contactId} className="border-b border-edge/60 last:border-0">
                  <td className="px-4 py-3 font-medium">{row.name}</td>
                  <td className="px-4 py-3 text-ink-muted">{row.phone}</td>
                  <td className="px-4 py-3 text-ink-muted">{fmtDateTime(row.firstMessageAt)}</td>
                  <td className="px-4 py-3 text-ink-muted">{fmtDateTime(row.lastMessageAt)}</td>
                  <td className="px-4 py-3">{row.outcome ?? '—'}</td>
                  <td className="px-4 py-3 text-ink-muted">{row.labels.join(', ') || '—'}</td>
                  <td className="px-4 py-3">{row.sellerName ?? '—'}</td>
                  <td className="px-4 py-3 text-ink-muted">{row.sellerNumber ?? '—'}</td>
                  <td className="px-4 py-3">{row.numberBanned ? 'Sim' : 'Não'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      <ShareDialog
        open={shareOpen}
        onClose={() => setShareOpen(false)}
        filters={filters}
        rows={data?.items ?? []}
        total={total}
      />

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-sm text-ink-muted">
          <span>
            {first}–{last} de {total}
          </span>
          <div className="flex gap-2">
            <Button variant="ghost" disabled={page === 1} onClick={() => setPage((p) => p - 1)}>
              Anterior
            </Button>
            <Button variant="ghost" disabled={last >= total} onClick={() => setPage((p) => p + 1)}>
              Próxima
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
