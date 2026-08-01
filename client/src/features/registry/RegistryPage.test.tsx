import { describe, expect, it } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RegistryPage } from './RegistryPage'
import { renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer, seller } from '../../test/msw'
import type { PairingSessionDto } from '../../api/types'

const SESSION_ID = 'pair-1'

function session(overrides: Partial<PairingSessionDto> = {}): PairingSessionDto {
  return {
    id: SESSION_ID,
    sellerId: 's1',
    status: 'AwaitingScan',
    detectedPhone: null,
    detectedProfileName: null,
    error: null,
    requiresTransfer: false,
    requiresBannedConfirmation: false,
    currentOwnerName: null,
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    qr: { code: 'QRDATA', base64: 'data:image/png;base64,abc123', pairingCode: null },
    ...overrides,
  }
}

async function openPairing(status: PairingSessionDto) {
  const ana = seller('Ana')
  mswServer.use(
    http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
    http.post('/api/v1/sellers/:id/pairings', () => HttpResponse.json(session())),
    http.get(`/api/v1/pairings/${SESSION_ID}`, () => HttpResponse.json(status)),
  )

  renderWithProviders(<RegistryPage />)
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: 'Conectar WhatsApp' }))
  return user
}

describe('RegistryPage', () => {
  // O número não é mais digitado: quem diz qual WhatsApp é são os dados do
  // aparelho que leu o QR. Sem campo, não há como cadastrar um e conectar outro.
  it('não pede o telefone para conectar', async () => {
    const ana = seller('Ana')
    mswServer.use(http.get('/api/v1/sellers', () => HttpResponse.json([ana])))

    renderWithProviders(<RegistryPage />)

    expect(await screen.findByRole('button', { name: 'Conectar WhatsApp' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Novo número para Ana')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Adicionar número' })).not.toBeInTheDocument()
  })

  // Iniciada a sessão, o QR aparece e a tela fica esperando o aparelho.
  it('mostra o QR code da sessão de pareamento', async () => {
    await openPairing(session())

    const img = await screen.findByAltText('QR code de conexão do WhatsApp')
    expect(img).toHaveAttribute('src', 'data:image/png;base64,abc123')
  })

  // Conectado com número livre: a tela confirma qual WhatsApp entrou, já
  // formatado.
  it('mostra o número detectado quando conclui', async () => {
    await openPairing(session({
      status: 'Completed',
      detectedPhone: '5511912344567',
      detectedProfileName: 'Igor',
      qr: null,
    }))

    const done = await screen.findByTestId('pairing-done')
    expect(done).toHaveTextContent('+55 11 91234-4567')
    expect(done).toHaveTextContent('Igor')
  })

  // Número de outro vendedor: a tela avisa de quem é e pede confirmação antes de
  // transferir — o histórico anterior continua com o dono antigo.
  it('pede confirmação para transferir um número de outro vendedor', async () => {
    let confirmed = false
    const user = await openPairing(session({
      status: 'AwaitingConfirmation',
      detectedPhone: '5511912344567',
      requiresTransfer: true,
      currentOwnerName: 'Bruno',
      qr: null,
    }))

    mswServer.use(
      http.post(`/api/v1/pairings/${SESSION_ID}/confirm`, () => {
        confirmed = true
        return HttpResponse.json(session({ status: 'Completed', qr: null }))
      }),
    )

    const box = await screen.findByTestId('pairing-confirm')
    expect(box).toHaveTextContent('Bruno')
    expect(box).toHaveTextContent('+55 11 91234-4567')

    await user.click(screen.getByRole('button', { name: 'Confirmar' }))
    await waitFor(() => expect(confirmed).toBe(true))
  })

  // Número banido pede o aviso extra: reativar por engano apagaria a decisão de
  // quem marcou o ban.
  it('avisa quando o número está banido', async () => {
    await openPairing(session({
      status: 'AwaitingConfirmation',
      detectedPhone: '5511912344567',
      requiresTransfer: true,
      requiresBannedConfirmation: true,
      currentOwnerName: 'Bruno',
      qr: null,
    }))

    expect(await screen.findByText(/banido permanentemente/)).toBeInTheDocument()
  })

  // Recusa do servidor (número já conectado, já cadastrado) aparece com o motivo
  // real, não com um erro genérico.
  it('mostra o motivo quando o pareamento é recusado', async () => {
    await openPairing(session({
      status: 'Rejected',
      error: 'Este WhatsApp já está conectado no vendedor Bruno.',
      qr: null,
    }))

    expect(await screen.findByText('Este WhatsApp já está conectado no vendedor Bruno.')).toBeInTheDocument()
  })

  // Um pareamento por vez: com outro em andamento, o servidor recusa e a tela
  // mostra o motivo em vez de abrir um QR que não vale.
  it('mostra o erro quando já existe um pareamento em andamento', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.post('/api/v1/sellers/:id/pairings', () =>
        HttpResponse.json({ error: 'Já existe um pareamento em andamento.' }, { status: 409 }),
      ),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Conectar WhatsApp' }))

    expect(await screen.findByText('Já existe um pareamento em andamento.')).toBeInTheDocument()
  })

  // Vendedor inativo tem o card esmaecido; o botão precisa estar desativado de
  // verdade. (Regressão: parecia indisponível e mesmo assim conectava.)
  it('não deixa conectar WhatsApp em vendedor inativo', async () => {
    mswServer.use(http.get('/api/v1/sellers', () => HttpResponse.json([seller('Ana', false)])))

    renderWithProviders(<RegistryPage />)

    expect(await screen.findByRole('button', { name: 'Conectar WhatsApp' })).toBeDisabled()
    expect(screen.getByText('Vendedor inativo: reative para conectar um WhatsApp.')).toBeInTheDocument()
  })

})
