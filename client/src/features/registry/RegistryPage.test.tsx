import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RegistryPage } from './RegistryPage'
import { renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer, seller } from '../../test/msw'
import type { PairingSessionDto } from '../../api/types'

const SESSION_ID = 'pair-1'

function activeNumber(sellerId: string) {
  return {
    id: 'n1',
    sellerId,
    phone: '5511968608425',
    instanceName: 'mv-1',
    status: 'Active',
    createdAt: new Date().toISOString(),
    bannedUntil: null,
    sendingPausedUntil: null,
    sendingPauseReason: null,
  }
}

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
    currentlyConnected: false,
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

  // Número conectado em outro vendedor agora também é oferecido para transferir,
  // com o aviso de que confirmar desliga o WhatsApp de lá. (Antes era recusado
  // sem saída.)
  it('avisa quando o número está conectado em outro vendedor', async () => {
    await openPairing(session({
      status: 'AwaitingConfirmation',
      detectedPhone: '5511912344567',
      requiresTransfer: true,
      currentlyConnected: true,
      currentOwnerName: 'Bruno',
      qr: null,
    }))

    expect(await screen.findByText(/conectado agora/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar' })).toBeInTheDocument()
  })

  // Transferir um número entre vendedores é decisão de quem opera: escolhe o
  // destino numa lista e confirma. O dono atual não entra na lista.
  it('transfere o número para outro vendedor', async () => {
    const ana = seller('Ana')
    const bruno = seller('Bruno')
    let transferredTo: string | null = null

    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana, bruno])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () =>
        HttpResponse.json([
          {
            id: 'n1',
            sellerId: ana.id,
            phone: '5511968608425',
            instanceName: 'mv-1',
            status: 'Active',
            createdAt: new Date().toISOString(),
          },
        ]),
      ),
      http.get(`/api/v1/sellers/${bruno.id}/numbers`, () => HttpResponse.json([])),
      http.post('/api/v1/numbers/n1/transfer', async ({ request }) => {
        transferredTo = ((await request.json()) as { sellerId: string }).sellerId
        return HttpResponse.json({})
      }),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    // Ação rara: mora no menu "⋯", não na linha.
    await user.click(await screen.findByRole('button', { name: /Mais ações para/ }))
    await user.click(await screen.findByRole('menuitem', { name: 'Transferir para outro vendedor' }))

    const select = await screen.findByLabelText('Novo vendedor')
    // Só o outro vendedor é destino possível.
    expect(within(select).queryByRole('option', { name: 'Ana' })).not.toBeInTheDocument()

    await user.selectOptions(select, bruno.id)
    await user.click(screen.getByRole('button', { name: 'Confirmar transferência' }))

    await waitFor(() => expect(transferredTo).toBe(bruno.id))
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

  // Quem abre o painel no próprio celular não tem uma segunda câmera para ler o
  // QR da tela: pede o código de pareamento informando o número. O número aqui é
  // só o destinatário do código, não o cadastro.
  it('gera o código de pareamento como alternativa ao QR', async () => {
    const user = await openPairing(session())

    await user.type(await screen.findByLabelText('Número para o código de pareamento'), '11968608425')

    mswServer.use(
      http.post(`/api/v1/pairings/${SESSION_ID}/pairing-code`, () =>
        HttpResponse.json(session({ qr: { code: 'QR2', base64: null, pairingCode: 'WZTK-9RQ2' } })),
      ),
    )
    await user.click(screen.getByRole('button', { name: 'Gerar código' }))

    expect(await screen.findByTestId('pairing-code')).toHaveTextContent('WZTK-9RQ2')
    expect(
      screen.getByText(/Use o código de pareamento em WhatsApp → Aparelhos conectados/),
    ).toBeInTheDocument()
  })

  // Número sem DDD não gera código, e o motivo vem do servidor.
  it('mostra o motivo quando o número do código é inválido', async () => {
    const user = await openPairing(session())

    mswServer.use(
      http.post(`/api/v1/pairings/${SESSION_ID}/pairing-code`, () =>
        HttpResponse.json({ error: 'Informe o número com DDD, por exemplo 11 91234-4567.' }, { status: 400 }),
      ),
    )
    await user.click(await screen.findByRole('button', { name: 'Gerar código' }))

    expect(
      await screen.findByText('Informe o número com DDD, por exemplo 11 91234-4567.'),
    ).toBeInTheDocument()
  })

  // Na reconexão o código não vem junto do QR: ele é pedido no clique, porque
  // gerá-lo recria a instância na Evolution. (Regressão: vinha automático, de uma
  // sessão antiga em cache, e o WhatsApp recusava.)
  it('só gera o código da reconexão quando pedido', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () =>
        HttpResponse.json([
          {
            id: 'n1',
            sellerId: ana.id,
            phone: '5511968608425',
            instanceName: 'mv-1',
            status: 'Disconnected',
            createdAt: new Date().toISOString(),
          },
        ]),
      ),
      http.post('/api/v1/numbers/n1/connect', () =>
        HttpResponse.json({ code: 'QRDATA', base64: null, pairingCode: null }),
      ),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Reconectar' }))

    // Abriu o QR e nenhum código veio junto.
    expect(await screen.findByRole('button', { name: 'Gerar código' })).toBeInTheDocument()
    expect(screen.queryByTestId('pairing-code')).not.toBeInTheDocument()

    mswServer.use(
      http.post('/api/v1/numbers/n1/pairing-code', () =>
        HttpResponse.json({ code: 'QR2', base64: null, pairingCode: 'ZLKPFRXL' }),
      ),
    )
    await user.click(screen.getByRole('button', { name: 'Gerar código' }))

    expect(await screen.findByTestId('pairing-code')).toHaveTextContent('ZLKPFRXL')
  })

  // O semáforo de saúde entra na linha do número com rótulo textual (nunca só
  // cor) e o "?" traz os sinais que pesaram no score.
  it('mostra o semáforo de saúde do número', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () => HttpResponse.json([activeNumber(ana.id)])),
      http.get('/api/v1/numbers/health', () =>
        HttpResponse.json([
          {
            numberId: 'n1',
            phone: '5511968608425',
            sellerId: ana.id,
            sellerName: 'Ana',
            status: 'Active',
            score: 30,
            level: 'Medium',
            signals: [{ key: 'delivery', value: '50%', points: 30 }],
          },
        ]),
      ),
    )

    renderWithProviders(<RegistryPage />)

    expect(await screen.findByText('Saúde: atenção')).toBeInTheDocument()
  })

  // Número em cooldown pós-ban: a linha avisa o prazo e reconectar exige
  // confirmação — que segue para a API como confirmCooldown=true.
  it('avisa o cooldown pós-ban e reconecta só depois de confirmar', async () => {
    const ana = seller('Ana')
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    let requestedUrl: string | null = null

    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () =>
        HttpResponse.json([
          {
            ...activeNumber(ana.id),
            status: 'BannedTemporary',
            bannedUntil: new Date(Date.now() + 20 * 3_600_000).toISOString(),
          },
        ]),
      ),
      http.post('/api/v1/numbers/n1/connect', ({ request }) => {
        requestedUrl = request.url
        return HttpResponse.json({ code: 'QRDATA', base64: null, pairingCode: null })
      }),
    )

    renderWithProviders(<RegistryPage />)
    expect(await screen.findByText(/Aguarde até/)).toBeInTheDocument()

    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Reconectar' }))

    await waitFor(() => expect(requestedUrl).toContain('confirmCooldown=true'))
    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining('ban permanente'))
    confirmSpy.mockRestore()
  })

  // Desconectar desvincula o aparelho: tira o vendedor do ar no meio do
  // expediente, então pede confirmação antes.
  it('desconecta o número depois de confirmar', async () => {
    let disconnected = false
    const ana = seller('Ana')
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)

    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () => HttpResponse.json([activeNumber(ana.id)])),
      http.post('/api/v1/numbers/n1/disconnect', () => {
        disconnected = true
        return HttpResponse.json({})
      }),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Desconectar' }))

    await waitFor(() => expect(disconnected).toBe(true))
    expect(confirmSpy).toHaveBeenCalled()
    confirmSpy.mockRestore()
  })

  // Recusar a confirmação não desconecta nada.
  it('não desconecta se a confirmação for recusada', async () => {
    let disconnected = false
    const ana = seller('Ana')
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)

    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () => HttpResponse.json([activeNumber(ana.id)])),
      http.post('/api/v1/numbers/n1/disconnect', () => {
        disconnected = true
        return HttpResponse.json({})
      }),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Desconectar' }))

    expect(disconnected).toBe(false)
    confirmSpy.mockRestore()
  })

  // Reiniciar não desvincula, então vai direto — sem confirmação.
  it('reinicia o número sem pedir confirmação', async () => {
    let restarted = false
    const ana = seller('Ana')
    const confirmSpy = vi.spyOn(window, 'confirm')

    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () => HttpResponse.json([activeNumber(ana.id)])),
      http.post('/api/v1/numbers/n1/restart', () => {
        restarted = true
        return HttpResponse.json({})
      }),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Reiniciar' }))

    await waitFor(() => expect(restarted).toBe(true))
    expect(confirmSpy).not.toHaveBeenCalled()
    confirmSpy.mockRestore()
  })

  // O socket sobe rápido demais para o clique dar sinal de vida: o círculo de
  // progresso fica no ar por ~1s e só então o botão volta ao normal.
  it('mostra o círculo de progresso enquanto reinicia', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () => HttpResponse.json([activeNumber(ana.id)])),
      http.post('/api/v1/numbers/n1/restart', () => HttpResponse.json({})),
    )

    renderWithProviders(<RegistryPage />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Reiniciar' }))

    // Entra o círculo e o botão trava — mas o nome acessível segue "Reiniciar":
    // o círculo é decorativo e quem anuncia o estado é o aria-busy.
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Reiniciar' })).toHaveAttribute('aria-busy', 'true'),
    )
    expect(screen.getByRole('button', { name: 'Reiniciar' })).toBeDisabled()

    // Passado o tempo, volta ao normal mesmo com a resposta tendo chegado antes.
    await waitFor(
      () => expect(screen.getByRole('button', { name: 'Reiniciar' })).not.toHaveAttribute('aria-busy'),
      { timeout: 3000 },
    )
    expect(screen.getByRole('button', { name: 'Reiniciar' })).toBeEnabled()
  })

  // Número que não está conectado não tem o que desconectar: o botão nem aparece.
  it('não oferece desconectar em número já desconectado', async () => {
    const ana = seller('Ana')
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      http.get(`/api/v1/sellers/${ana.id}/numbers`, () =>
        HttpResponse.json([{ ...activeNumber(ana.id), status: 'Disconnected' }]),
      ),
    )

    renderWithProviders(<RegistryPage />)

    expect(await screen.findByRole('button', { name: 'Reiniciar' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Desconectar' })).not.toBeInTheDocument()
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
