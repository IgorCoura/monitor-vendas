import { useState } from 'react'
import {
  useBanPermanent,
  useCancelPairing,
  useConfirmPairing,
  useConnectNumber,
  useDisconnectNumber,
  useCreateSeller,
  useNumberPairingCode,
  useNumbers,
  usePairingStatus,
  useRequestPairingCode,
  useRestartNumber,
  useSellers,
  useStartPairing,
  useTransferNumber,
  useUpdateSeller,
} from '../../api/queries'
import type { QrCodeDto, SellerResponse } from '../../api/types'
import {
  Button,
  Card,
  Dialog,
  EmptyState,
  ErrorState,
  Input,
  Menu,
  ProgressCircle,
  Select,
  Spinner,
  StatusBadge,
} from '../../components/ui'
import { ApiError } from '../../api/client'
import { fmtPhone } from '../../lib/format'

type ReconnectTarget = { numberId: string; confirmBanned: boolean; qr: QrCodeDto }

function QrDialog({ target, onClose }: { target: ReconnectTarget | null; onClose: () => void }) {
  const requestCode = useNumberPairingCode()
  const [code, setCode] = useState<string | null>(null)
  const [codeError, setCodeError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const qr = code ? null : target?.qr
  const base64 = qr?.base64
  const src = base64 ? (base64.startsWith('data:') ? base64 : `data:image/png;base64,${base64}`) : null

  function close() {
    setCode(null)
    setCodeError(null)
    setCopied(false)
    onClose()
  }

  // O código de pareamento é pedido SOB DEMANDA porque gerá-lo recria a
  // instância na Evolution: fazer isso a cada reconexão trocaria a instância de
  // quem só queria o QR. E pedido a uma instância já existente, o código vem de
  // uma sessão antiga em cache — o WhatsApp recusa.
  async function generate() {
    if (!target) return
    setCodeError(null)
    try {
      const fresh = await requestCode.mutateAsync({
        id: target.numberId,
        confirmBanned: target.confirmBanned,
      })
      setCode(fresh.pairingCode ?? null)
      if (!fresh.pairingCode) setCodeError('A Evolution não devolveu um código para este número.')
    } catch (err) {
      setCodeError(err instanceof ApiError ? err.message : 'Não foi possível gerar o código.')
    }
  }

  async function copyCode() {
    if (!code) return
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <Dialog open={target !== null} onClose={close} title="Escaneie o QR code no WhatsApp">
      {src && (
        // 256px fixos estouravam a folha num iPhone SE (320px de tela): a
        // imagem acompanha a largura disponível, sem passar do tamanho de antes.
        <img
          src={src}
          alt="QR code de conexão do WhatsApp"
          className="mx-auto w-[min(16rem,70vw)] md:w-64"
        />
      )}
      {!src && !code && (
        <p className="text-sm break-all">{target?.qr.code ?? 'QR indisponível — tente reconectar.'}</p>
      )}
      {!code && (
        <p className="mt-3 text-xs text-ink-muted">
          WhatsApp → Aparelhos conectados → Conectar aparelho. O QR expira rápido; gere outro se
          precisar.
        </p>
      )}

      {/* Quem abre o painel pelo celular está com o telefone na mão — não tem
          uma segunda câmera para ler o QR da própria tela. */}
      <div className="mt-4 rounded-lg bg-surface p-3">
        {code ? (
          <>
            <p className="text-xs font-medium text-ink-muted">
              No mesmo aparelho? Use o código de pareamento em WhatsApp → Aparelhos conectados →
              Conectar com número de telefone.
            </p>
            <div className="mt-2 flex items-center gap-2">
              <code
                data-testid="pairing-code"
                className="flex-1 text-base font-semibold tracking-widest"
              >
                {code}
              </code>
              <Button variant="ghost" onClick={copyCode} className="shrink-0">
                {copied ? 'Copiado' : 'Copiar'}
              </Button>
            </div>
          </>
        ) : (
          <>
            <p className="text-xs font-medium text-ink-muted">
              No mesmo aparelho? Gere um código de pareamento para conectar sem ler o QR. O QR acima
              deixa de valer.
            </p>
            <Button
              variant="ghost"
              onClick={generate}
              disabled={requestCode.isPending}
              className="mt-2"
            >
              Gerar código
            </Button>
          </>
        )}
        {codeError && <p className="mt-2 text-xs text-danger">{codeError}</p>}
      </div>
    </Dialog>
  )
}

type TransferTarget = { numberId: string; phone: string; sellerId: string }

// Transferir um número entre vendedores. O passado NÃO se move: o histórico
// carimbado continua com quem atendeu, e o novo dono responde daqui para frente.
function TransferDialog({
  target,
  onClose,
}: {
  target: TransferTarget | null
  onClose: () => void
}) {
  const { data: sellers } = useSellers()
  const transfer = useTransferNumber()
  const [sellerId, setSellerId] = useState('')
  const [error, setError] = useState<string | null>(null)

  // O dono atual não é destino possível.
  const options = (sellers ?? []).filter((s) => s.id !== target?.sellerId)

  function close() {
    setSellerId('')
    setError(null)
    onClose()
  }

  async function confirm() {
    if (!target || !sellerId) return
    setError(null)
    try {
      await transfer.mutateAsync({ id: target.numberId, sellerId })
      close()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao transferir o número.')
    }
  }

  return (
    <Dialog
      open={target !== null}
      onClose={close}
      title={`Transferir ${target ? fmtPhone(target.phone) : ''}`}
      footer={
        <div className="flex justify-end gap-2 max-md:flex-col-reverse">
          <Button variant="ghost" onClick={close}>
            Cancelar
          </Button>
          <Button onClick={confirm} disabled={!sellerId || transfer.isPending}>
            Confirmar transferência
          </Button>
        </div>
      }
    >
      <div className="space-y-3 text-sm">
        <p className="text-ink-muted">
          O número passa a contar para o vendedor escolhido <strong>a partir de agora</strong>. As
          conversas e mensagens já registradas continuam com o vendedor atual.
        </p>
        <label className="block">
          <span className="text-xs font-medium text-ink-muted">Novo vendedor</span>
          <Select
            aria-label="Novo vendedor"
            className="mt-1"
            value={sellerId}
            onChange={(e) => setSellerId(e.target.value)}
          >
            <option value="">Selecione…</option>
            {options.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
                {s.active ? '' : ' (inativo)'}
              </option>
            ))}
          </Select>
        </label>
        {options.length === 0 && (
          <p className="text-xs text-ink-muted">Não há outro vendedor cadastrado para receber.</p>
        )}
        {error && <p className="text-xs text-danger">{error}</p>}
      </div>
    </Dialog>
  )
}

// Conduz uma tentativa de conexão: QR → o servidor descobre o número → e, se
// for o caso, pergunta o que fazer (transferir de vendedor, reativar banido).
function PairingDialog({
  sessionId,
  sellerName,
  onClose,
}: {
  sessionId: string | null
  sellerName: string
  onClose: () => void
}) {
  const { data: session } = usePairingStatus(sessionId)
  const confirmPairing = useConfirmPairing()
  const cancelPairing = useCancelPairing()
  const requestCode = useRequestPairingCode()

  const qr = session?.qr
  const base64 = qr?.base64
  const src = base64 ? (base64.startsWith('data:') ? base64 : `data:image/png;base64,${base64}`) : null
  const pairingCode = qr?.pairingCode ?? null

  const [phone, setPhone] = useState('')
  const [codeError, setCodeError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  async function askForCode() {
    if (!session) return
    setCodeError(null)
    try {
      await requestCode.mutateAsync({ id: session.id, phone })
    } catch (err) {
      setCodeError(err instanceof ApiError ? err.message : 'Não foi possível gerar o código.')
    }
  }

  async function copyCode() {
    if (!pairingCode) return
    try {
      await navigator.clipboard.writeText(pairingCode)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  async function close() {
    // Sessão ainda viva ao fechar: cancela para não deixar instância órfã na
    // Evolution nem a vaga de pareamento tomada.
    if (session && (session.status === 'AwaitingScan' || session.status === 'AwaitingConfirmation'))
      await cancelPairing.mutateAsync(session.id).catch(() => {})

    onClose()
  }

  return (
    <Dialog
      open={sessionId !== null}
      onClose={close}
      title={`Conectar WhatsApp de ${sellerName}`}
      footer={
        session?.status === 'AwaitingConfirmation' ? (
          <div className="flex justify-end gap-2 max-md:flex-col-reverse">
            <Button variant="ghost" onClick={close}>
              Não transferir
            </Button>
            <Button
              onClick={async () => {
                await confirmPairing.mutateAsync(session.id)
                onClose()
              }}
              disabled={confirmPairing.isPending}
            >
              Confirmar
            </Button>
          </div>
        ) : (
          <div className="flex justify-end">
            <Button variant="ghost" onClick={close}>
              Fechar
            </Button>
          </div>
        )
      }
    >
      {session?.status === 'AwaitingScan' && (
        <div className="space-y-3" data-testid="pairing-qr">
          {src ? (
            <img src={src} alt="QR code de conexão do WhatsApp" className="mx-auto h-56 w-56" />
          ) : (
            <Spinner />
          )}
          <p className="text-sm text-ink-muted">
            WhatsApp → Aparelhos conectados → Conectar aparelho. O número é lido do próprio
            aparelho: <strong>não é possível cadastrar um número e conectar outro</strong>.
          </p>

          {/* Quem abre o painel pelo celular está com o telefone na mão — não tem
              uma segunda câmera para ler o QR da própria tela. O número pedido
              aqui só diz ao WhatsApp para quem mandar o código; o cadastro
              continua saindo do aparelho que conectar. */}
          <div className="rounded-lg bg-surface p-3">
            {pairingCode ? (
              <>
                <p className="text-xs font-medium text-ink-muted">
                  No mesmo aparelho? Use o código de pareamento em WhatsApp → Aparelhos conectados →
                  Conectar com número de telefone.
                </p>
                <div className="mt-2 flex items-center gap-2">
                  <code
                    data-testid="pairing-code"
                    className="flex-1 text-base font-semibold tracking-widest"
                  >
                    {pairingCode}
                  </code>
                  <Button variant="ghost" onClick={copyCode} className="shrink-0">
                    {copied ? 'Copiado' : 'Copiar'}
                  </Button>
                </div>
              </>
            ) : (
              <>
                <p className="text-xs font-medium text-ink-muted">
                  No mesmo aparelho? Informe o número para receber um código de pareamento em vez do
                  QR. O número serve só para o WhatsApp saber a quem mandar o código — o cadastro
                  continua sendo o do aparelho que conectar.
                </p>
                <div className="mt-2 flex items-end gap-2 max-md:flex-col max-md:items-stretch">
                  <Input
                    aria-label="Número para o código de pareamento"
                    placeholder="11 91234-4567"
                    inputMode="tel"
                    value={phone}
                    onChange={(e) => setPhone(e.target.value)}
                  />
                  <Button
                    variant="ghost"
                    onClick={askForCode}
                    disabled={requestCode.isPending}
                    className="shrink-0"
                  >
                    Gerar código
                  </Button>
                </div>
                {codeError && <p className="mt-2 text-xs text-danger">{codeError}</p>}
              </>
            )}
          </div>
        </div>
      )}

      {session?.status === 'AwaitingConfirmation' && (
        <div className="space-y-2 text-sm" data-testid="pairing-confirm">
          <p>
            Conectado: <strong>{fmtPhone(session.detectedPhone)}</strong>
            {session.detectedProfileName && ` (${session.detectedProfileName})`}
          </p>
          {session.requiresTransfer && (
            <p>
              Este número já está cadastrado em <strong>{session.currentOwnerName}</strong>. Confirmar
              transfere o número para {sellerName}; o histórico anterior continua com{' '}
              {session.currentOwnerName}.
            </p>
          )}
          {session.currentlyConnected && (
            <p className="text-danger">
              Atenção: ele está <strong>conectado agora</strong> em {session.currentOwnerName}.
              Confirmar desliga o WhatsApp de lá.
            </p>
          )}
          {session.requiresBannedConfirmation && (
            <p className="text-danger">
              Atenção: este número está marcado como <strong>banido permanentemente</strong>.
              Confirmar reativa o número.
            </p>
          )}
        </div>
      )}

      {session?.status === 'Completed' && (
        <div className="space-y-2 text-sm" data-testid="pairing-done">
          <p>
            WhatsApp conectado: <strong>{fmtPhone(session.detectedPhone)}</strong>
            {session.detectedProfileName && ` (${session.detectedProfileName})`}
          </p>
          <p className="text-ink-muted">As mensagens deste número já estão sendo monitoradas.</p>
        </div>
      )}

      {session?.status === 'Rejected' && (
        <ErrorState message={session.error ?? 'Não foi possível conectar este WhatsApp.'} />
      )}
    </Dialog>
  )
}

function SellerCard({ seller }: { seller: SellerResponse }) {
  const { data: numbers, isLoading } = useNumbers(seller.id)
  const updateSeller = useUpdateSeller()
  const connectNumber = useConnectNumber()
  const banPermanent = useBanPermanent()
  const disconnectNumber = useDisconnectNumber()
  const restartNumber = useRestartNumber()
  const startPairing = useStartPairing()

  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(seller.name)
  const [reconnect, setReconnect] = useState<ReconnectTarget | null>(null)
  const [pairingId, setPairingId] = useState<string | null>(null)
  const [transfer, setTransfer] = useState<TransferTarget | null>(null)
  const [restarting, setRestarting] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Conectar não pede telefone: quem diz o número é o WhatsApp que ler o QR.
  async function handleStartPairing() {
    setError(null)
    try {
      const session = await startPairing.mutateAsync(seller.id)
      setPairingId(session.id)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao iniciar a conexão.')
    }
  }

  async function handleConnect(id: string, status: string) {
    setError(null)

    // Ban permanente foi decisão de quem opera; reconectar sem confirmar
    // apagaria isso em silêncio.
    const confirmBanned = status === 'BannedPermanent'
    if (confirmBanned && !window.confirm(
      'Este número está marcado como banido permanentemente. Deseja mesmo reconectá-lo?',
    )) return

    try {
      const qr = await connectNumber.mutateAsync({ id, confirmBanned })
      setReconnect({ numberId: id, confirmBanned, qr })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao gerar novo QR.')
    }
  }

  // Desconectar tira o vendedor do ar no meio do expediente: pede confirmação,
  // como o ban.
  async function handleDisconnect(id: string, phoneLabel: string) {
    if (!window.confirm(
      `Desconectar ${fmtPhone(phoneLabel)}? O WhatsApp sai do ar e só volta lendo um QR code novo.`,
    )) return

    setError(null)
    try {
      await disconnectNumber.mutateAsync(id)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao desconectar o número.')
    }
  }

  // Reiniciar é inócuo (não desvincula), então vai sem confirmação. O socket sobe
  // rápido demais para dar sinal de que algo aconteceu: o círculo fica no ar por
  // pelo menos 1s, senão o clique parece não ter feito nada.
  async function handleRestart(id: string) {
    setError(null)
    setRestarting(id)
    try {
      await Promise.all([
        restartNumber.mutateAsync(id),
        new Promise((resolve) => setTimeout(resolve, 1000)),
      ])
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao reiniciar o número.')
    } finally {
      setRestarting(null)
    }
  }

  async function handleBan(id: string, phoneLabel: string) {
    if (!window.confirm(`Marcar ${phoneLabel} como banido permanentemente?`)) return
    try {
      await banPermanent.mutateAsync(id)
    } catch {
      setError('Falha ao marcar o ban permanente.')
    }
  }

  return (
    <Card className={seller.active ? '' : 'opacity-60'}>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        {editing ? (
          <form
            className="flex flex-1 flex-col gap-2 sm:flex-row"
            onSubmit={async (e) => {
              e.preventDefault()
              await updateSeller.mutateAsync({ id: seller.id, name, active: seller.active })
              setEditing(false)
            }}
          >
            <Input value={name} onChange={(e) => setName(e.target.value)} aria-label="Nome do vendedor" />
            <Button type="submit">Salvar</Button>
          </form>
        ) : (
          <p className="font-semibold">
            {seller.name}
            {!seller.active && <span className="ml-2 text-xs text-ink-muted">(inativo)</span>}
          </p>
        )}
        <div className="flex shrink-0 gap-1">
          {!editing && (
            <Button variant="ghost" onClick={() => setEditing(true)}>
              Renomear
            </Button>
          )}
          <Button
            variant="ghost"
            onClick={() =>
              updateSeller.mutate({ id: seller.id, name: seller.name, active: !seller.active })
            }
          >
            {seller.active ? 'Desativar' : 'Ativar'}
          </Button>
        </div>
      </div>

      {isLoading && <Spinner />}
      <ul className="space-y-2">
        {(numbers ?? []).map((n) => (
          <li key={n.id} className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-surface px-3 py-2">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium">{fmtPhone(n.phone)}</span>
              <StatusBadge status={n.status} />
            </div>
            {/* Frequentes na linha; raros e destrutivos no "⋯" — com os cinco
                visíveis a linha virava um bloco de botões no celular. */}
            <div className="flex gap-1">
              <Button variant="ghost" onClick={() => handleConnect(n.id, n.status)}>
                Reconectar
              </Button>
              {n.status === 'Active' && (
                <Button variant="ghost" onClick={() => handleDisconnect(n.id, n.phone)}>
                  Desconectar
                </Button>
              )}
              {/* O rótulo acessível não muda com o círculo: quem usa leitor de
                  tela (e o teste) continua achando o mesmo botão. */}
              <Button
                variant="ghost"
                aria-label="Reiniciar"
                onClick={() => handleRestart(n.id)}
                disabled={restarting === n.id}
              >
                {restarting === n.id ? <ProgressCircle label="Reiniciando" /> : 'Reiniciar'}
              </Button>
              <Menu
                label={`Mais ações para ${fmtPhone(n.phone)}`}
                actions={[
                  {
                    label: 'Transferir para outro vendedor',
                    onSelect: () =>
                      setTransfer({ numberId: n.id, phone: n.phone, sellerId: seller.id }),
                  },
                  ...(n.status === 'BannedPermanent'
                    ? []
                    : [
                        {
                          label: 'Marcar ban permanente',
                          danger: true,
                          onSelect: () => handleBan(n.id, n.phone),
                        },
                      ]),
                ]}
              />
            </div>
          </li>
        ))}
      </ul>

      {/* Sem campo de telefone: digitar o número permitia cadastrar um e parear
          outro, e o histórico ficava pendurado no número errado. */}
      {/* Vendedor inativo tem o card inteiro esmaecido; o botão precisa estar
          desativado de verdade, senão ele parece indisponível e funciona. */}
      <Button
        className="mt-3 w-full md:w-auto"
        onClick={handleStartPairing}
        disabled={startPairing.isPending || !seller.active}
        title={seller.active ? undefined : 'Reative o vendedor para conectar um WhatsApp.'}
      >
        Conectar WhatsApp
      </Button>
      {!seller.active && (
        <p className="mt-2 text-xs text-ink-muted">
          Vendedor inativo: reative para conectar um WhatsApp.
        </p>
      )}
      {error && <p className="mt-2 text-xs text-danger">{error}</p>}

      <QrDialog target={reconnect} onClose={() => setReconnect(null)} />
      <TransferDialog target={transfer} onClose={() => setTransfer(null)} />
      <PairingDialog
        sessionId={pairingId}
        sellerName={seller.name}
        onClose={() => setPairingId(null)}
      />
    </Card>
  )
}

export function RegistryPage() {
  const { data: sellers, isLoading, isError } = useSellers()
  const createSeller = useCreateSeller()
  const [newName, setNewName] = useState('')

  return (
    <div className="space-y-6">
      <h2 className="text-xl font-bold">Cadastros</h2>

      <Card>
        <form
          className="flex flex-col gap-2 md:flex-row"
          onSubmit={async (e) => {
            e.preventDefault()
            if (!newName.trim()) return
            await createSeller.mutateAsync(newName.trim())
            setNewName('')
          }}
        >
          <Input
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            placeholder="Nome do novo vendedor"
            aria-label="Nome do novo vendedor"
            className="flex-1"
          />
          <Button type="submit" disabled={createSeller.isPending}>
            Cadastrar vendedor
          </Button>
        </form>
      </Card>

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar os vendedores." />}
      {sellers?.length === 0 && <EmptyState message="Nenhum vendedor cadastrado ainda." />}

      <div className="grid gap-4 lg:grid-cols-2">
        {(sellers ?? []).map((seller) => (
          <SellerCard key={seller.id} seller={seller} />
        ))}
      </div>
    </div>
  )
}
