import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RegistryPage } from './RegistryPage'
import { renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer, seller } from '../../test/msw'

describe('RegistryPage', () => {
  // Cadastrar um número mostra o dialog com a imagem do QR devolvido pela API.
  it('exibe o QR code após cadastrar um número', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.post('/api/v1/sellers/:id/numbers', () =>
        HttpResponse.json(
          {
            number: {
              id: crypto.randomUUID(),
              sellerId: ana.id,
              phone: '5511999999999',
              instanceName: 'mv-5511999999999',
              status: 'Disconnected',
              createdAt: new Date().toISOString(),
            },
            qr: { code: 'QRDATA', base64: 'data:image/png;base64,abc123', pairingCode: null },
          },
          { status: 201 },
        ),
      ),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()

    const input = await screen.findByLabelText('Novo número para Ana')
    await user.type(input, '5511999999999')
    await user.click(screen.getByRole('button', { name: 'Adicionar número' }))

    const img = await screen.findByAltText('QR code de conexão do WhatsApp')
    expect(img).toHaveAttribute('src', 'data:image/png;base64,abc123')
  })

  // Telefone duplicado (409 da API) mostra a mensagem de erro no card do vendedor.
  it('mostra o erro da API quando o telefone já existe', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.post('/api/v1/sellers/:id/numbers', () =>
        HttpResponse.json({ error: 'Este telefone já está cadastrado.' }, { status: 409 }),
      ),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()

    await user.type(await screen.findByLabelText('Novo número para Ana'), '5511999999999')
    await user.click(screen.getByRole('button', { name: 'Adicionar número' }))

    expect(await screen.findByText('Este telefone já está cadastrado.')).toBeInTheDocument()
  })
})
