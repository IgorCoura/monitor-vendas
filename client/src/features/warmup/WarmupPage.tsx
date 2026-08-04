import { useState } from 'react'
import { ApiError } from '../../api/client'
import { useHaltWarmup, useToggleWarmup, useWarmup, useWarmupPeer } from '../../api/queries'
import type { WarmupConversationDto, WarmupPeerDto } from '../../api/types'
import { KpiCard } from '../../components/KpiCard'
import { Button, Card, Dialog, EmptyState, ErrorState, InfoTip, Spinner } from '../../components/ui'
import { ExpandableMetricCard, type MetricItem } from '../../components/mobile/MetricList'
import { useIsMobile } from '../../lib/useIsMobile'
import { fmtDateTime, fmtPercent, fmtPhone } from '../../lib/format'
import { warmupHelp } from '../../lib/metrics'

const conversationStatus: Record<string, { label: string; className: string }> = {
  Scheduled: { label: 'Agendada', className: 'bg-surface text-ink-muted' },
  Running: { label: 'Em andamento', className: 'bg-ok-soft text-ok' },
  Completed: { label: 'Concluída', className: 'bg-ok-soft text-ok' },
  Abandoned: { label: 'Abandonada', className: 'bg-warn-soft text-warn' },
  Failed: { label: 'Falhou', className: 'bg-danger-soft text-danger' },
}

function StatusBadge({ status }: { status: string }) {
  const style = conversationStatus[status] ?? { label: status, className: 'bg-surface text-ink-muted' }
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${style.className}`}>
      {style.label}
    </span>
  )
}

function peerItems(peer: WarmupPeerDto): MetricItem[] {
  return [
    { key: 'seller', label: 'Vendedor', value: peer.sellerName },
    {
      key: 'today',
      label: 'Hoje',
      value: `${peer.warmupMessagesToday} de ${peer.effectiveGoal}`,
      help: warmupHelp.meta,
    },
    { key: 'real', label: 'Com clientes hoje', value: peer.realMessagesToday, help: warmupHelp.real },
    {
      key: 'circle',
      label: 'Círculo',
      value: `${peer.coreCircle} próximos · ${peer.occasionalCircle} ocasionais`,
      help: warmupHelp.circulo,
    },
    { key: 'persona', label: 'Jeito de escrever', value: peer.persona ?? '—' },
  ]
}

// Com quem este número conversa. Sem isso "círculo: 3" é um número sem sentido
// para quem olha a tela.
function Circle({ peer }: { peer: WarmupPeerDto }) {
  if (peer.circle.length === 0)
    return <p className="text-xs text-ink-muted">Ainda sem colegas: o círculo cresce um por semana.</p>

  return (
    <p className="text-xs text-ink-muted">
      Conversa com {peer.circle.map((phone) => fmtPhone(phone)).join(', ')}.
    </p>
  )
}

// A conversa inteira, com o texto de cada mensagem: monitorar o aquecimento sem
// poder ler o que ele mandou seria confiar no escuro.
function ConversationDialog({
  conversation,
  onClose,
}: {
  conversation: WarmupConversationDto | null
  onClose: () => void
}) {
  return (
    <Dialog
      open={conversation !== null}
      onClose={onClose}
      title={conversation ? `Conversa sobre ${conversation.theme}` : ''}
      footer={
        <div className="flex justify-end">
          <Button variant="ghost" onClick={onClose}>
            Fechar
          </Button>
        </div>
      }
    >
      {conversation && (
        <div className="space-y-3" data-testid="warmup-conversation">
          <p className="text-xs text-ink-muted">
            {fmtPhone(conversation.phoneA)} e {fmtPhone(conversation.phoneB)} ·{' '}
            {conversation.archived ? 'arquivada nos dois lados' : 'ainda não arquivada'}
          </p>
          <ul className="space-y-2">
            {conversation.turns.map((turn) => (
              <li
                key={turn.sequence}
                className={turn.fromPhone === conversation.phoneA ? 'text-left' : 'text-right'}
              >
                <span className="inline-block max-w-[85%] rounded-2xl bg-surface px-3 py-2 text-sm">
                  {turn.text}
                </span>
                <p className="mt-0.5 text-[11px] text-ink-muted">
                  {fmtPhone(turn.fromPhone)} ·{' '}
                  {turn.sentAt
                    ? `${fmtDateTime(turn.sentAt)}${turn.delivered ? ' · entregue' : ' · sem confirmação'}`
                    : `sai ${fmtDateTime(turn.scheduledAt)}`}
                </p>
              </li>
            ))}
          </ul>
        </div>
      )}
    </Dialog>
  )
}

export function WarmupPage() {
  const isMobile = useIsMobile()
  const { data, isLoading, isError } = useWarmup()
  const toggle = useToggleWarmup()
  const halt = useHaltWarmup()
  const peer = useWarmupPeer()

  const [open, setOpen] = useState<WarmupConversationDto | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [confirmHalt, setConfirmHalt] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function run(action: Promise<unknown>) {
    setError(null)
    try {
      await action
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao falar com o servidor.')
    }
  }

  const running = data?.enabled === true && data.haltedAt === null

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-bold">Aquecimento</h2>
        <div className="flex flex-wrap items-center gap-1">
          <Button variant="ghost" onClick={() => setConfirmHalt(true)} disabled={!running}>
            Parar tudo agora
          </Button>
          <Button
            variant={running ? 'ghost' : 'primary'}
            onClick={() => run(toggle.mutateAsync(!data?.enabled))}
            loading={toggle.isPending}
          >
            {data?.enabled ? 'Desligar aquecimento' : 'Ligar aquecimento'}
          </Button>
        </div>
      </div>

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar o aquecimento." />}
      {error && <ErrorState message={error} />}

      {data && (
        <>
          {data.haltedAt && (
            <Card className="border-danger/40 bg-danger-soft" data-testid="warmup-halted">
              <p className="text-sm font-semibold text-danger">
                Aquecimento parado em {fmtDateTime(data.haltedAt)}.
              </p>
              <p className="mt-1 text-sm text-danger">{data.haltReason}</p>
              <p className="mt-1 text-xs text-danger">
                Religar é decisão manual: use "Ligar aquecimento" quando entender o que aconteceu.
              </p>
            </Card>
          )}

          {data.idleReason && (
            <Card className="border-edge bg-card" data-testid="warmup-idle">
              <p className="text-sm">
                <span className="font-semibold">Nada sendo agendado agora.</span> {data.idleReason}
              </p>
            </Card>
          )}

          {!data.enabled && !data.haltedAt && (
            <Card className="border-warn/40 bg-warn-soft">
              <p className="text-sm text-warn">
                Aquecimento desligado: nenhuma mensagem entre os números é gerada ou enviada.
              </p>
            </Card>
          )}

          <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
            <KpiCard label="Números no pool" value={String(data.peersInPool)} help={warmupHelp.pool} />
            <KpiCard label="Mensagens hoje" value={String(data.messagesToday)} help={warmupHelp.mensagens} />
            <KpiCard
              label="Conversas hoje"
              value={String(data.conversationsToday)}
              help={warmupHelp.conversas}
            />
            <KpiCard
              label="Taxa de entrega"
              value={fmtPercent(data.deliveryRate)}
              help={warmupHelp.entrega}
            />
          </div>

          <section className="space-y-3">
            <h3 className="text-sm font-semibold tracking-wide text-ink-muted uppercase">Números</h3>

            {data.numbers.length === 0 && (
              <EmptyState message="Nenhum número cadastrado. Conecte um WhatsApp em Cadastros." />
            )}

            {isMobile && data.numbers.length > 0 && (
              <div className="space-y-3" data-testid="warmup-cards">
                {data.numbers.map((n) => (
                  <ExpandableMetricCard
                    key={n.numberId}
                    data-testid={`warmup-card-${n.numberId}`}
                    title={
                      <span className="flex flex-wrap items-center gap-2">
                        {fmtPhone(n.phone)}
                        {n.inPool ? (
                          <span className="rounded-full bg-ok-soft px-2.5 py-0.5 text-xs font-semibold text-ok">
                            No pool
                          </span>
                        ) : (
                          <span className="rounded-full bg-surface px-2.5 py-0.5 text-xs font-semibold text-ink-muted">
                            Fora
                          </span>
                        )}
                      </span>
                    }
                    moreLabel="ver círculo"
                    items={peerItems(n)}
                    details={
                      <div className="space-y-2">
                        <Circle peer={n} />
                        {n.ineligibleReason && (
                          <p className="text-xs text-warn">Não participa agora: {n.ineligibleReason}.</p>
                        )}
                        <Button
                          variant="ghost"
                          onClick={() =>
                            run(peer.mutateAsync({ numberId: n.numberId, inPool: !n.inPool }))
                          }
                          loading={peer.isPending && peer.variables?.numberId === n.numberId}
                        >
                          {n.inPool ? 'Tirar do aquecimento' : 'Colocar no aquecimento'}
                        </Button>
                      </div>
                    }
                  />
                ))}
              </div>
            )}

            {!isMobile && data.numbers.length > 0 && (
              <Card className="overflow-x-auto p-0">
                <table data-testid="warmup-table" className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-edge text-left text-xs tracking-wide text-ink-muted uppercase">
                      <th className="px-4 py-3">Número</th>
                      <th className="px-4 py-3">Vendedor</th>
                      <th className="px-4 py-3">
                        <span className="flex items-center gap-1.5">
                          Hoje <InfoTip text={warmupHelp.meta} />
                        </span>
                      </th>
                      <th className="px-4 py-3">
                        <span className="flex items-center gap-1.5">
                          Círculo <InfoTip text={warmupHelp.circulo} />
                        </span>
                      </th>
                      <th className="px-4 py-3"></th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.numbers.map((n) => (
                      <tr key={n.numberId} className="border-b border-edge/60 align-top last:border-0">
                        <td className="px-4 py-3">
                          <button
                            className="text-left font-medium underline decoration-dotted"
                            onClick={() => setExpanded(expanded === n.numberId ? null : n.numberId)}
                            aria-expanded={expanded === n.numberId}
                          >
                            {fmtPhone(n.phone)}
                          </button>
                          {n.ineligibleReason && (
                            <p className="text-xs text-warn">{n.ineligibleReason}</p>
                          )}
                          {expanded === n.numberId && (
                            <div className="mt-2">
                              <Circle peer={n} />
                            </div>
                          )}
                        </td>
                        <td className="px-4 py-3 text-ink-muted">{n.sellerName}</td>
                        <td className="px-4 py-3">
                          {n.inPool ? (
                            <>
                              {n.warmupMessagesToday} de {n.effectiveGoal}
                              <p className="text-xs text-ink-muted">
                                {n.realMessagesToday} com clientes
                                {n.cappedByGraph && (
                                  <span className="text-warn">
                                    {' '}
                                    · meta {n.goal} capada pelo pool <InfoTip text={warmupHelp.capado} />
                                  </span>
                                )}
                              </p>
                            </>
                          ) : (
                            <span className="text-ink-muted">—</span>
                          )}
                        </td>
                        <td className="px-4 py-3 text-ink-muted">
                          {n.inPool ? `${n.coreCircle} + ${n.occasionalCircle}` : '—'}
                        </td>
                        <td className="px-4 py-3">
                          <div className="flex justify-end">
                            <Button
                              variant="ghost"
                              onClick={() =>
                                run(peer.mutateAsync({ numberId: n.numberId, inPool: !n.inPool }))
                              }
                              loading={peer.isPending && peer.variables?.numberId === n.numberId}
                            >
                              {n.inPool ? 'Tirar' : 'Colocar'}
                            </Button>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </Card>
            )}
          </section>

          <section className="space-y-3">
            <h3 className="text-sm font-semibold tracking-wide text-ink-muted uppercase">
              Conversas recentes
            </h3>

            {data.conversations.length === 0 ? (
              <EmptyState message="Nenhuma conversa ainda. Com o aquecimento ligado, a primeira sai em minutos." />
            ) : (
              <Card className="p-0">
                <ul data-testid="warmup-conversations" className="divide-y divide-edge/60">
                  {data.conversations.map((c) => (
                    <li key={c.id}>
                      <button
                        className="flex w-full flex-wrap items-center justify-between gap-2 px-4 py-3 text-left"
                        onClick={() => setOpen(c)}
                      >
                        <span className="min-w-0">
                          <span className="block truncate text-sm font-medium">{c.theme}</span>
                          <span className="block text-xs text-ink-muted">
                            {fmtPhone(c.phoneA)} e {fmtPhone(c.phoneB)} · {c.turns.length} mensagens ·{' '}
                            {fmtDateTime(c.createdAt)}
                          </span>
                        </span>
                        <span className="flex items-center gap-2">
                          {c.archived && <span className="text-xs text-ink-muted">arquivada</span>}
                          <StatusBadge status={c.status} />
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              </Card>
            )}
          </section>
        </>
      )}

      <ConversationDialog conversation={open} onClose={() => setOpen(null)} />

      <Dialog
        open={confirmHalt}
        onClose={() => setConfirmHalt(false)}
        title="Parar o aquecimento agora"
        footer={
          <div className="flex justify-end gap-2 max-md:flex-col-reverse">
            <Button variant="ghost" onClick={() => setConfirmHalt(false)}>
              Cancelar
            </Button>
            <Button
              onClick={async () => {
                await run(halt.mutateAsync())
                setConfirmHalt(false)
              }}
              loading={halt.isPending}
            >
              Parar tudo
            </Button>
          </div>
        }
      >
        <p className="text-sm">
          Nenhuma mensagem nova é gerada ou enviada, em nenhum número do pool. As conversas já
          agendadas ficam paradas onde estão.
        </p>
        <p className="mt-2 text-sm text-ink-muted">
          Voltar a rodar é decisão manual: o interruptor continua ligado, mas nada sai até você
          religar.
        </p>
      </Dialog>
    </div>
  )
}
