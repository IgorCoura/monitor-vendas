import { useState } from 'react'
import { ApiError } from '../../api/client'
import {
  useCompleteWarmup,
  usePauseWarmup,
  useRestartWarmup,
  useWarmup,
} from '../../api/queries'
import type { WarmupNumberDto, WarmupState } from '../../api/types'
import { KpiCard } from '../../components/KpiCard'
import { Button, Card, EmptyState, ErrorState, Menu, Spinner } from '../../components/ui'
import { ExpandableMetricCard, type MetricItem } from '../../components/mobile/MetricList'
import { useIsMobile } from '../../lib/useIsMobile'
import { fmtPhone } from '../../lib/format'
import { warmupHelp, warmupStateLabel } from '../../lib/metrics'

function StateBadge({ state, atCeiling }: { state: WarmupState; atCeiling: boolean }) {
  const style: Record<WarmupState, string> = {
    Warming: 'bg-warn-soft text-warn',
    Paused: 'bg-surface text-ink-muted',
    Mature: 'bg-ok-soft text-ok',
    NoData: 'bg-surface text-ink-muted',
  }

  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${style[state]}`}>
        {warmupStateLabel[state]}
      </span>
      {/* O motivo pelo qual o número parou de enviar, dito com todas as letras. */}
      {atCeiling && (
        <span className="rounded-full bg-danger-soft px-2.5 py-0.5 text-xs font-semibold text-danger">
          Teto do dia atingido
        </span>
      )}
    </span>
  )
}

// "Dia 5 de 30" com a barra: a curva é progressiva, e ver a posição explica o
// teto melhor que o número solto.
function CurveProgress({ number }: { number: WarmupNumberDto }) {
  if (number.state === 'Mature' || number.state === 'NoData')
    return <span className="text-xs text-ink-muted">—</span>

  const pct = number.totalDays > 0 ? Math.min(100, (number.day / number.totalDays) * 100) : 0

  return (
    <div className="min-w-28">
      <p className="text-xs font-medium">
        Dia {number.day} de {number.totalDays}
      </p>
      <div className="mt-1 h-1.5 w-full rounded-full bg-surface">
        <div className="h-1.5 rounded-full bg-primary" style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

function usage(number: WarmupNumberDto): string {
  return number.messagesPerDay === null
    ? `${number.messagesToday} (sem teto)`
    : `${number.messagesToday}/${number.messagesPerDay}`
}

export function WarmupPage() {
  const isMobile = useIsMobile()
  const { data, isLoading, isError } = useWarmup()
  const restart = useRestartWarmup()
  const pause = usePauseWarmup()
  const complete = useCompleteWarmup()

  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [curveOpen, setCurveOpen] = useState(false)

  // As ações respondem rápido demais para dar sinal: o círculo fica no ar por
  // pelo menos 1s, senão o clique parece não ter feito nada.
  async function run(id: string, action: () => Promise<unknown>, fallback: string) {
    setError(null)
    setBusy(id)
    try {
      await Promise.all([action(), new Promise((r) => setTimeout(r, 1000))])
    } catch (err) {
      setError(err instanceof ApiError ? err.message : fallback)
    } finally {
      setBusy(null)
    }
  }

  function handleComplete(number: WarmupNumberDto) {
    if (!window.confirm(
      `Marcar ${fmtPhone(number.phone)} como aquecido? O teto progressivo deixa de valer e o número ` +
      'passa a poder enviar no volume cheio imediatamente.',
    )) return

    void run(number.numberId, () => complete.mutateAsync(number.numberId), 'Falha ao concluir o aquecimento.')
  }

  function actionsFor(number: WarmupNumberDto) {
    return [
      {
        label: 'Reiniciar curva (voltar ao dia 1)',
        onSelect: () =>
          void run(number.numberId, () => restart.mutateAsync(number.numberId), 'Falha ao reiniciar a curva.'),
      },
      ...(number.state === 'Mature' || number.state === 'NoData'
        ? []
        : [
            {
              label: number.state === 'Paused' ? 'Retomar aquecimento' : 'Pausar aquecimento',
              onSelect: () =>
                void run(
                  number.numberId,
                  () => pause.mutateAsync({ id: number.numberId, paused: number.state !== 'Paused' }),
                  'Falha ao pausar o aquecimento.',
                ),
            },
            {
              label: 'Marcar como aquecido',
              danger: true,
              onSelect: () => handleComplete(number),
            },
          ]),
    ]
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-bold">Aquecimento</h2>
        <Button variant="ghost" onClick={() => setCurveOpen((v) => !v)} aria-expanded={curveOpen}>
          {curveOpen ? 'Ocultar a curva' : 'Ver a curva'}
        </Button>
      </div>

      <Card>
        <p className="text-sm text-ink-muted">
          O aquecimento <strong>não envia nada</strong>: ele limita o quanto cada número pode enviar por dia
          enquanto é novo. Número banido volta ao dia 1 — retomar o volume de antes é o caminho mais curto
          para o próximo ban.
        </p>
      </Card>

      {curveOpen && data && (
        <Card data-testid="warmup-curve">
          <h3 className="mb-2 text-sm font-semibold">Curva configurada</h3>
          <ul className="space-y-1 text-xs text-ink-muted">
            {data.curve.map((step, i) => (
              <li key={step.throughDay}>
                <span className="font-medium text-ink">
                  Dia {i === 0 ? 1 : data.curve[i - 1].throughDay + 1}–{step.throughDay}:
                </span>{' '}
                até {step.messagesPerDay} mensagens/dia e {step.newContactsPerDay} contatos novos/dia
              </li>
            ))}
            <li>
              <span className="font-medium text-ink">Depois:</span> sem teto de aquecimento (vale a cota normal)
            </li>
          </ul>
        </Card>
      )}

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar o aquecimento." />}
      {error && <ErrorState message={error} />}

      {data && (
        <>
          <div className="grid grid-cols-3 gap-4">
            <KpiCard label="Em aquecimento" value={String(data.warming)} help={warmupHelp.emAquecimento} />
            <KpiCard label="Aquecidos" value={String(data.mature)} help={warmupHelp.aquecidos} />
            <KpiCard label="No teto hoje" value={String(data.atCeiling)} help={warmupHelp.noTeto} />
          </div>

          {data.numbers.length === 0 && <EmptyState message="Nenhum número cadastrado ainda." />}

          {isMobile && data.numbers.length > 0 && (
            <div className="space-y-3" data-testid="warmup-cards">
              {data.numbers.map((n) => (
                <ExpandableMetricCard
                  key={n.numberId}
                  data-testid={`warmup-card-${n.numberId}`}
                  title={
                    <span className="flex flex-wrap items-center gap-2">
                      {fmtPhone(n.phone)}
                      <StateBadge state={n.state} atCeiling={n.atCeiling} />
                    </span>
                  }
                  moreLabel="ver detalhes"
                  items={[
                    { key: 'seller', label: 'Vendedor', value: n.sellerName },
                    {
                      key: 'day',
                      label: 'Dia da curva',
                      value: n.state === 'Mature' || n.state === 'NoData' ? '—' : `${n.day} de ${n.totalDays}`,
                      help: warmupHelp.dia,
                    },
                    { key: 'usage', label: 'Enviadas hoje', value: usage(n), help: warmupHelp.enviadas },
                    {
                      key: 'contacts',
                      label: 'Contatos novos hoje',
                      value: `${n.newContactsToday}/${n.newContactsPerDay}`,
                      help: warmupHelp.novosContatos,
                    },
                  ] satisfies MetricItem[]}
                  details={<Menu label={`Ações de aquecimento para ${fmtPhone(n.phone)}`} actions={actionsFor(n)} />}
                />
              ))}
            </div>
          )}

          {!isMobile && data.numbers.length > 0 && (
            <Card className="overflow-x-auto p-0">
              <table data-testid="warmup-table" className="w-full text-sm">
                <thead>
                  <tr className="border-b border-edge text-left text-xs uppercase tracking-wide text-ink-muted">
                    <th className="px-4 py-3">Número</th>
                    <th className="px-4 py-3">Estado</th>
                    <th className="px-4 py-3">Curva</th>
                    <th className="px-4 py-3">Enviadas hoje</th>
                    <th className="px-4 py-3">Contatos novos</th>
                    <th className="px-4 py-3"></th>
                  </tr>
                </thead>
                <tbody>
                  {data.numbers.map((n) => (
                    <tr
                      key={n.numberId}
                      className={`border-b border-edge/60 last:border-0 ${n.atCeiling ? 'bg-danger-soft/40' : ''}`}
                    >
                      <td className="px-4 py-3">
                        <p className="font-medium">{fmtPhone(n.phone)}</p>
                        <p className="text-xs text-ink-muted">{n.sellerName}</p>
                      </td>
                      <td className="px-4 py-3">
                        <StateBadge state={n.state} atCeiling={n.atCeiling} />
                      </td>
                      <td className="px-4 py-3">
                        <CurveProgress number={n} />
                      </td>
                      <td className="px-4 py-3 font-medium">{usage(n)}</td>
                      <td className="px-4 py-3 text-ink-muted">
                        {n.newContactsToday}/{n.newContactsPerDay}
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex justify-end">
                          {busy === n.numberId ? (
                            <Button variant="ghost" loading>
                              Aplicando
                            </Button>
                          ) : (
                            <Menu
                              label={`Ações de aquecimento para ${fmtPhone(n.phone)}`}
                              actions={actionsFor(n)}
                            />
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>
          )}
        </>
      )}
    </div>
  )
}
