import { useState } from 'react'
import { ApiError } from '../../api/client'
import { useAllNumbers, useContactShareStatus, useCreateContactShare } from '../../api/queries'
import type { ContactFilters, ContactRowDto } from '../../api/types'
import { Button, Dialog, ErrorState, Input, Select } from '../../components/ui'
import { fmtPhone } from '../../lib/format'

// Mesma regra do servidor (ContactMessageBuilder): contato sem nome salvo sai só
// com o número. Aqui é só o exemplo do formato — o texto real é montado lá.
function sampleLine(row: ContactRowDto): string {
  return row.name === row.phone ? row.phone : `${row.name} - ${row.phone}`
}

export function ShareDialog({
  open,
  onClose,
  filters,
  rows,
  total,
}: {
  open: boolean
  onClose: () => void
  filters: ContactFilters
  rows: ContactRowDto[]
  total: number
}) {
  const { data: numbers } = useAllNumbers()
  const createShare = useCreateContactShare()
  const [senderNumberId, setSenderNumberId] = useState('')
  const [destination, setDestination] = useState('')
  const [shareId, setShareId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const { data: share } = useContactShareStatus(shareId)

  const active = (numbers ?? []).filter((n) => n.status === 'Active')
  const sender = senderNumberId || active[0]?.id || ''

  function close() {
    setShareId(null)
    setError(null)
    onClose()
  }

  async function send() {
    setError(null)
    try {
      const created = await createShare.mutateAsync({ filters, senderNumberId: sender, destination })
      setShareId(created.id)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao iniciar o envio.')
    }
  }

  const sendDisabled = createShare.isPending || destination.replace(/\D/g, '').length < 10

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Enviar contatos por WhatsApp"
      // No rodapé do dialog os botões ficam fora da área rolável: no celular,
      // com o teclado aberto, eles continuam alcançáveis.
      footer={
        share || active.length === 0 ? undefined : (
          <div className="flex justify-end gap-2 max-md:flex-col-reverse">
            <Button variant="ghost" onClick={close}>
              Cancelar
            </Button>
            <Button onClick={send} disabled={sendDisabled}>
              Enviar
            </Button>
          </div>
        )
      }
    >
      {share ? (
        <div className="space-y-3" data-testid="share-progress">
          <p className="text-sm">
            {share.status === 'Pending' && `Enviando… ${share.sentMessages}/${share.totalMessages} mensagens.`}
            {share.status === 'Completed' &&
              `Enviado: ${share.totalContacts} contatos em ${share.totalMessages} ${share.totalMessages === 1 ? 'mensagem' : 'mensagens'}.`}
            {share.status === 'Failed' && `Falhou: ${share.error ?? 'erro no envio.'}`}
          </p>
          <p className="text-xs text-ink-muted">Destino: {share.destination}</p>
          <Button onClick={close}>Fechar</Button>
        </div>
      ) : (
        <div className="space-y-4">
          {active.length === 0 ? (
            <ErrorState message="Nenhum número conectado para enviar. Conecte um número em Cadastros." />
          ) : (
            <>
              <label className="flex flex-col gap-1 text-xs font-medium text-ink-muted">
                Enviar pelo número
                <Select
                  aria-label="Enviar pelo número"
                  value={sender}
                  onChange={(e) => setSenderNumberId(e.target.value)}
                >
                  {active.map((number) => (
                    <option key={number.id} value={number.id}>
                      {fmtPhone(number.phone)} — {number.sellerName}
                    </option>
                  ))}
                </Select>
              </label>

              <label className="flex flex-col gap-1 text-xs font-medium text-ink-muted">
                Enviar para
                <Input
                  aria-label="Enviar para"
                  value={destination}
                  onChange={(e) => setDestination(e.target.value)}
                  placeholder="5511999999999"
                  inputMode="numeric"
                />
              </label>

              <div className="rounded-lg bg-surface p-3">
                <p className="text-xs font-medium text-ink-muted">
                  {total} {total === 1 ? 'contato' : 'contatos'} no filtro atual. Formato:
                </p>
                <pre className="mt-1 text-xs whitespace-pre-wrap text-ink">
                  {rows.slice(0, 3).map(sampleLine).join('\n')}
                  {rows.length > 3 ? '\n…' : ''}
                </pre>
              </div>

              {error && <ErrorState message={error} />}
            </>
          )}
        </div>
      )}
    </Dialog>
  )
}
