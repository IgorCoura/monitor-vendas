import { useState, type FormEvent } from 'react'
import {
  useBanPermanent,
  useConnectNumber,
  useCreateNumber,
  useCreateSeller,
  useNumbers,
  useSellers,
  useUpdateSeller,
} from '../../api/queries'
import type { QrCodeDto, SellerResponse } from '../../api/types'
import { Button, Card, Dialog, EmptyState, ErrorState, Input, Spinner, StatusBadge } from '../../components/ui'
import { ApiError } from '../../api/client'

function QrDialog({ qr, onClose }: { qr: QrCodeDto | null; onClose: () => void }) {
  const base64 = qr?.base64
  const src = base64 ? (base64.startsWith('data:') ? base64 : `data:image/png;base64,${base64}`) : null
  const pairingCode = qr?.pairingCode ?? null
  const [copied, setCopied] = useState(false)

  async function copyPairingCode() {
    if (!pairingCode) return
    try {
      await navigator.clipboard.writeText(pairingCode)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <Dialog open={qr !== null} onClose={onClose} title="Escaneie o QR code no WhatsApp">
      {src ? (
        // 256px fixos estouravam a folha num iPhone SE (320px de tela): a
        // imagem acompanha a largura disponível, sem passar do tamanho de antes.
        <img
          src={src}
          alt="QR code de conexão do WhatsApp"
          className="mx-auto w-[min(16rem,70vw)] md:w-64"
        />
      ) : (
        <p className="text-sm break-all">{qr?.code ?? pairingCode ?? 'QR indisponível — tente reconectar.'}</p>
      )}
      <p className="mt-3 text-xs text-ink-muted">
        WhatsApp → Aparelhos conectados → Conectar aparelho. O QR expira rápido; gere outro se precisar.
      </p>

      {/* Quem abre o painel pelo celular está com o telefone na mão — não tem
          uma segunda câmera para ler o QR da própria tela. O código de
          pareamento é a saída: "Conectar com número de telefone" no WhatsApp. */}
      {pairingCode && (
        <div className="mt-4 rounded-lg bg-surface p-3">
          <p className="text-xs font-medium text-ink-muted">
            No mesmo aparelho? Use o código de pareamento em WhatsApp → Aparelhos conectados →
            Conectar com número de telefone.
          </p>
          <div className="mt-2 flex items-center gap-2">
            <code data-testid="pairing-code" className="flex-1 text-base font-semibold tracking-widest">
              {pairingCode}
            </code>
            <Button variant="ghost" onClick={copyPairingCode} className="shrink-0">
              {copied ? 'Copiado' : 'Copiar'}
            </Button>
          </div>
        </div>
      )}
    </Dialog>
  )
}

function SellerCard({ seller }: { seller: SellerResponse }) {
  const { data: numbers, isLoading } = useNumbers(seller.id)
  const updateSeller = useUpdateSeller()
  const createNumber = useCreateNumber()
  const connectNumber = useConnectNumber()
  const banPermanent = useBanPermanent()

  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(seller.name)
  const [phone, setPhone] = useState('')
  const [qr, setQr] = useState<QrCodeDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function handleAddNumber(e: FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      const created = await createNumber.mutateAsync({ sellerId: seller.id, phone })
      setPhone('')
      setQr(created.qr)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao cadastrar o número.')
    }
  }

  async function handleConnect(id: string) {
    setError(null)
    try {
      setQr(await connectNumber.mutateAsync(id))
    } catch {
      setError('Falha ao gerar novo QR.')
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
              <span className="text-sm font-medium">{n.phone}</span>
              <StatusBadge status={n.status} />
            </div>
            <div className="flex gap-1">
              <Button variant="ghost" onClick={() => handleConnect(n.id)}>
                Novo QR
              </Button>
              {n.status !== 'BannedPermanent' && (
                <Button variant="danger" onClick={() => handleBan(n.id, n.phone)}>
                  Ban permanente
                </Button>
              )}
            </div>
          </li>
        ))}
      </ul>

      {/* Em coluna no celular: lado a lado, o botão de texto longo espremia o
          campo do telefone até ele sumir. */}
      <form className="mt-3 flex flex-col gap-2 md:flex-row" onSubmit={handleAddNumber}>
        <Input
          value={phone}
          onChange={(e) => setPhone(e.target.value)}
          placeholder="Telefone com DDI (5511999999999)"
          aria-label={`Novo número para ${seller.name}`}
          inputMode="numeric"
          className="flex-1"
        />
        <Button type="submit" disabled={createNumber.isPending || phone.trim().length < 10}>
          Adicionar número
        </Button>
      </form>
      {error && <p className="mt-2 text-xs text-danger">{error}</p>}

      <QrDialog qr={qr} onClose={() => setQr(null)} />
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
