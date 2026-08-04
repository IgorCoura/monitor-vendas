import { useState } from 'react'
import { ApiError, api } from '../../api/client'
import {
  useApplyAllocation,
  useDetachProxy,
  usePauseProxy,
  useProxies,
  useSyncProxies,
  useTestProxy,
  useToggleProxies,
} from '../../api/queries'
import type { AllocationPreviewDto, ProxyDto } from '../../api/types'
import { KpiCard } from '../../components/KpiCard'
import {
  Button,
  Card,
  Dialog,
  EmptyState,
  ErrorState,
  Menu,
  Spinner,
} from '../../components/ui'
import { ExpandableMetricCard, type MetricItem } from '../../components/mobile/MetricList'
import { useIsMobile } from '../../lib/useIsMobile'
import { fmtDateTime, fmtPhone } from '../../lib/format'
import { proxyHelp } from '../../lib/metrics'

const statusLabel: Record<string, { label: string; className: string }> = {
  Active: { label: 'Ativo', className: 'bg-ok-soft text-ok' },
  Paused: { label: 'Pausado', className: 'bg-warn-soft text-warn' },
  Suspect: { label: 'Suspeito', className: 'bg-warn-soft text-warn' },
  Expired: { label: 'Vencido', className: 'bg-danger-soft text-danger' },
  Revoked: { label: 'Revogado', className: 'bg-danger-soft text-danger' },
  Failed: { label: 'Falhou', className: 'bg-danger text-white' },
}

// Badge sempre com rótulo textual, nunca só cor.
function ProxyStatusBadge({ status }: { status: string }) {
  const style = statusLabel[status] ?? { label: status, className: 'bg-surface text-ink-muted' }
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${style.className}`}>
      {style.label}
    </span>
  )
}

function proxyItems(proxy: ProxyDto): MetricItem[] {
  return [
    { key: 'numbers', label: 'Números', value: `${proxy.numbersCount}/${proxy.capacity}`, help: proxyHelp.ocupacao },
    { key: 'sellers', label: 'Vendedores', value: proxy.sellersCount, help: proxyHelp.vendedores },
    { key: 'bans', label: 'Bans no período', value: proxy.bansCount, help: proxyHelp.bans },
    { key: 'banned', label: 'Números banidos', value: proxy.bannedNumbersCount, help: proxyHelp.numerosBanidos },
    { key: 'endpoint', label: 'Endereço', value: `${proxy.host}:${proxy.port}` },
    { key: 'tested', label: 'Último teste', value: fmtDateTime(proxy.lastTestedAt) },
    { key: 'expires', label: 'Vence em', value: fmtDateTime(proxy.expiresAt) },
  ]
}

// Os números que estão neste proxy, para o expandido do card e da linha.
function ProxyNumbers({ proxy }: { proxy: ProxyDto }) {
  if (proxy.numbers.length === 0)
    return <p className="text-xs text-ink-muted">Nenhum número neste proxy.</p>

  return (
    <ul className="space-y-1 text-xs">
      {proxy.numbers.map((n) => (
        <li key={n.numberId} className="flex flex-wrap justify-between gap-2">
          <span className="font-medium">{fmtPhone(n.phone)}</span>
          <span className="text-ink-muted">
            {n.sellerName} · {n.status}
          </span>
        </li>
      ))}
    </ul>
  )
}

function PreviewDialog({
  open,
  onClose,
  rebalance,
}: {
  open: boolean
  onClose: () => void
  rebalance: boolean
}) {
  const apply = useApplyAllocation()
  const [preview, setPreview] = useState<AllocationPreviewDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  // O plano é buscado ao abrir: aplicar sem ver quantos sockets reiniciam seria
  // exatamente o que o dois-passos existe para evitar.
  async function load() {
    setLoading(true)
    setError(null)
    try {
      setPreview(await api.proxies.preview(rebalance))
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao calcular a distribuição.')
    } finally {
      setLoading(false)
    }
  }

  if (open && preview === null && !loading && error === null) void load()

  function close() {
    setPreview(null)
    setError(null)
    onClose()
  }

  const restarts = preview?.moves.filter((m) => m.restartsSocket).length ?? 0

  return (
    <Dialog
      open={open}
      onClose={close}
      title={rebalance ? 'Redistribuir números' : 'Distribuir números sem proxy'}
      footer={
        <div className="flex justify-end gap-2 max-md:flex-col-reverse">
          <Button variant="ghost" onClick={close}>
            Cancelar
          </Button>
          <Button
            onClick={async () => {
              await apply.mutateAsync(rebalance)
              close()
            }}
            disabled={!preview || preview.moves.length === 0}
            loading={apply.isPending}
          >
            Aplicar
          </Button>
        </div>
      }
    >
      {loading && <Spinner />}
      {error && <ErrorState message={error} />}
      {preview && (
        <div className="space-y-3" data-testid="allocation-preview">
          {preview.moves.length === 0 ? (
            <p className="text-sm">Nada a mudar: a distribuição já está como deveria.</p>
          ) : (
            <>
              <p className="text-sm">
                {preview.moves.length} {preview.moves.length === 1 ? 'número muda' : 'números mudam'} de proxy.
                {restarts > 0 && (
                  <span className="text-warn">
                    {' '}
                    {restarts} {restarts === 1 ? 'está conectado e reinicia' : 'estão conectados e reiniciam'} a
                    sessão.
                  </span>
                )}
              </p>
              <ul className="space-y-1 text-xs">
                {preview.moves.map((m) => (
                  <li key={m.numberId} className="flex flex-wrap justify-between gap-2">
                    <span className="font-medium">{fmtPhone(m.phone)}</span>
                    <span className="text-ink-muted">
                      {m.sellerName} · {m.fromLabel ?? 'sem proxy'} → {m.toLabel}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          )}
          {preview.stillWithoutProxy.length > 0 && (
            <p className="text-xs text-warn">
              {preview.stillWithoutProxy.length} número(s) continuam sem proxy: não há vaga. Contrate mais
              proxies no portal do fornecedor.
            </p>
          )}
        </div>
      )}
    </Dialog>
  )
}

export function ProxiesPage() {
  const isMobile = useIsMobile()
  const { data, isLoading, isError } = useProxies()
  const toggle = useToggleProxies()
  const sync = useSyncProxies()
  const test = useTestProxy()
  const pause = usePauseProxy()
  const detach = useDetachProxy()

  const [testing, setTesting] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [preview, setPreview] = useState<{ open: boolean; rebalance: boolean }>({
    open: false,
    rebalance: false,
  })
  const [error, setError] = useState<string | null>(null)

  // O teste responde rápido demais para dar sinal: o círculo fica no ar por
  // pelo menos 1s, senão o clique parece não ter feito nada.
  async function handleTest(id: string) {
    setError(null)
    setTesting(id)
    try {
      await Promise.all([test.mutateAsync(id), new Promise((r) => setTimeout(r, 1000))])
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao testar o proxy.')
    } finally {
      setTesting(null)
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-bold">Proxies</h2>
        <div className="flex flex-wrap items-center gap-1">
          <Button variant="ghost" onClick={() => sync.mutate()} loading={sync.isPending}>
            Sincronizar
          </Button>
          <Button
            variant="ghost"
            onClick={() => setPreview({ open: true, rebalance: false })}
            disabled={!data || data.numbersWithoutProxy === 0}
          >
            Distribuir números
          </Button>
          <Button variant="ghost" onClick={() => setPreview({ open: true, rebalance: true })}>
            Redistribuir
          </Button>
          <Button
            variant={data?.enabled ? 'ghost' : 'primary'}
            onClick={() => toggle.mutate(!data?.enabled)}
            loading={toggle.isPending}
          >
            {data?.enabled ? 'Desligar proxies' : 'Ligar proxies'}
          </Button>
        </div>
      </div>

      {data && !data.enabled && (
        <Card className="border-warn/40 bg-warn-soft">
          <p className="text-sm text-warn">
            Uso de proxies desligado: números novos nascem sem proxy. Os já conectados continuam nos seus
            proxies até reconectarem — tirar todos de uma vez reiniciaria todas as sessões juntas.
          </p>
        </Card>
      )}

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar os proxies." />}
      {error && <ErrorState message={error} />}

      {data && (
        <>
          <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
            <KpiCard label="Proxies ativos" value={String(data.activeProxies)} help={proxyHelp.ativos} />
            <KpiCard label="Números com proxy" value={String(data.assignedNumbers)} help={proxyHelp.atribuidos} />
            <KpiCard
              label="Números sem proxy"
              value={String(data.numbersWithoutProxy)}
              help={proxyHelp.semProxy}
            />
            <KpiCard label="Bans no período" value={String(data.bansInPeriod)} help={proxyHelp.bans} />
          </div>

          {data.numbersWithoutProxy > 0 && (
            <Card className="border-warn/40 bg-warn-soft" data-testid="without-proxy">
              <p className="text-sm text-warn">
                {data.numbersWithoutProxy} número(s) sem proxy:{' '}
                {data.unassigned.map((n) => fmtPhone(n.phone)).join(', ')}.
              </p>
            </Card>
          )}

          {data.proxies.length === 0 && (
            <EmptyState message="Nenhum proxy sincronizado. Contrate no portal do fornecedor e clique em Sincronizar." />
          )}

          {isMobile && data.proxies.length > 0 && (
            <div className="space-y-3" data-testid="proxies-cards">
              {data.proxies.map((proxy) => (
                <ExpandableMetricCard
                  key={proxy.id}
                  data-testid={`proxy-card-${proxy.id}`}
                  title={
                    <span className="flex flex-wrap items-center gap-2">
                      {proxy.label}
                      <ProxyStatusBadge status={proxy.status} />
                    </span>
                  }
                  moreLabel="ver números"
                  items={proxyItems(proxy)}
                  details={<ProxyNumbers proxy={proxy} />}
                />
              ))}
            </div>
          )}

          {!isMobile && data.proxies.length > 0 && (
            <Card className="overflow-x-auto p-0">
              <table data-testid="proxies-table" className="w-full text-sm">
                <thead>
                  <tr className="border-b border-edge text-left text-xs uppercase tracking-wide text-ink-muted">
                    <th className="px-4 py-3">Proxy</th>
                    <th className="px-4 py-3">Status</th>
                    <th className="px-4 py-3">Números</th>
                    <th className="px-4 py-3">Vendedores</th>
                    <th className="px-4 py-3">Bans</th>
                    <th className="px-4 py-3">Último teste</th>
                    <th className="px-4 py-3"></th>
                  </tr>
                </thead>
                <tbody>
                  {data.proxies.map((proxy) => (
                    <tr key={proxy.id} className="border-b border-edge/60 last:border-0 align-top">
                      <td className="px-4 py-3">
                        <button
                          className="text-left font-medium underline decoration-dotted"
                          onClick={() => setExpanded(expanded === proxy.id ? null : proxy.id)}
                          aria-expanded={expanded === proxy.id}
                        >
                          {proxy.label}
                        </button>
                        <p className="text-xs text-ink-muted">
                          {proxy.host}:{proxy.port} · {proxy.kind}
                        </p>
                        {expanded === proxy.id && (
                          <div className="mt-2">
                            <ProxyNumbers proxy={proxy} />
                          </div>
                        )}
                      </td>
                      <td className="px-4 py-3">
                        <ProxyStatusBadge status={proxy.status} />
                      </td>
                      <td className="px-4 py-3">
                        {proxy.numbersCount}/{proxy.capacity}
                      </td>
                      <td className="px-4 py-3 text-ink-muted">{proxy.sellersCount}</td>
                      <td className="px-4 py-3 text-ink-muted">
                        {proxy.bansCount}
                        {proxy.bannedNumbersCount > 0 && ` (${proxy.bannedNumbersCount} nº)`}
                      </td>
                      <td className="px-4 py-3 text-ink-muted">
                        {fmtDateTime(proxy.lastTestedAt)}
                        {proxy.lastTestOk === false && <span className="text-danger"> · falhou</span>}
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex justify-end gap-1">
                          <Button
                            variant="ghost"
                            onClick={() => handleTest(proxy.id)}
                            loading={testing === proxy.id}
                          >
                            Testar
                          </Button>
                          <Menu
                            label={`Mais ações para ${proxy.label}`}
                            actions={[
                              {
                                label: proxy.status === 'Paused' ? 'Retomar' : 'Pausar',
                                onSelect: () =>
                                  pause.mutate({ id: proxy.id, paused: proxy.status !== 'Paused' }),
                              },
                              ...proxy.numbers.map((n) => ({
                                label: `Tirar ${fmtPhone(n.phone)} deste proxy`,
                                danger: true,
                                onSelect: () => detach.mutate(n.numberId),
                              })),
                            ]}
                          />
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

      <PreviewDialog
        open={preview.open}
        rebalance={preview.rebalance}
        onClose={() => setPreview({ open: false, rebalance: false })}
      />
    </div>
  )
}
