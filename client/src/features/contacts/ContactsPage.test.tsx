import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ContactsPage } from './ContactsPage'
import { renderMobile, renderWithProviders } from '../../test/render'
import { contactRow, http, HttpResponse, mswServer, seller } from '../../test/msw'

function contactsHandler(onRequest?: (url: URL) => void) {
  return http.get('/api/v1/contacts', ({ request }) => {
    onRequest?.(new URL(request.url))
    return HttpResponse.json({
      items: [
        contactRow('Maria Silva', {
          outcomeTypeCode: 'sale',
          outcome: 'Vendas',
          labels: ['venda'],
        }),
        contactRow('João Souza', { sellerName: 'Bruno', numberBanned: true, numberStatus: 'BannedPermanent' }),
      ],
      page: 1,
      pageSize: 50,
      total: 2,
    })
  })
}

describe('ContactsPage', () => {
  // A tabela mostra uma linha por contato com desfecho tratado e a marca de banimento.
  it('lista os contatos com desfecho e banimento', async () => {
    mswServer.use(contactsHandler())

    renderWithProviders(<ContactsPage />)

    const table = await screen.findByTestId('contacts-table')
    expect(within(table).getByText('Maria Silva')).toBeInTheDocument()
    expect(within(table).getByText('Vendas')).toBeInTheDocument()
    expect(within(table).getByText('venda')).toBeInTheDocument()
    expect(within(table).getByText('Bruno')).toBeInTheDocument()
    expect(within(table).getByText('Sim')).toBeInTheDocument()
  })

  // Sem contatos no filtro, mostra o estado vazio em vez de tabela vazia.
  it('mostra estado vazio', async () => {
    renderWithProviders(<ContactsPage />)

    expect(await screen.findByText('Nenhum contato com os filtros atuais.')).toBeInTheDocument()
  })

  // Escolher vendedor e desfecho vai para a API como sellerId/outcomeTypes.
  it('envia os filtros escolhidos para a API', async () => {
    const ana = seller('Ana')
    let lastUrl: URL | null = null
    mswServer.use(
      http.get('/api/v1/sellers', () => HttpResponse.json([ana])),
      contactsHandler((url) => {
        lastUrl = url
      }),
    )

    renderWithProviders(<ContactsPage />)
    const user = userEvent.setup()
    await screen.findByTestId('contacts-table')

    await user.selectOptions(screen.getByLabelText('Vendedor'), ana.id)
    await user.click(screen.getByRole('button', { name: 'Vendas' }))
    await user.click(screen.getByRole('button', { name: 'Número banido' }))

    await waitFor(() => {
      expect(lastUrl!.searchParams.get('sellerId')).toBe(ana.id)
      expect(lastUrl!.searchParams.get('outcomeTypes')).toBe('sale')
      expect(lastUrl!.searchParams.get('banned')).toBe('true')
    })
  })

  // O envio manda remetente, destino e os filtros atuais, e a tela passa a mostrar
  // o progresso devolvido pela API.
  it('envia a lista por WhatsApp e acompanha o progresso', async () => {
    let posted: { url?: URL; body?: { senderNumberId: string; destination: string } } = {}
    mswServer.use(
      contactsHandler(),
      http.get('/api/v1/numbers', () =>
        HttpResponse.json([
          { id: 'num-1', phone: '5511900002222', status: 'Active', sellerId: 's1', sellerName: 'Ana' },
          { id: 'num-2', phone: '5511900003333', status: 'BannedPermanent', sellerId: 's2', sellerName: 'Bruno' },
        ]),
      ),
      http.post('/api/v1/contacts/share', async ({ request }) => {
        posted = {
          url: new URL(request.url),
          body: (await request.json()) as { senderNumberId: string; destination: string },
        }
        return HttpResponse.json(
          {
            id: 'share-1',
            senderNumberId: 'num-1',
            senderPhone: '5511900002222',
            destination: '5511777770000',
            totalContacts: 2,
            totalMessages: 1,
            sentMessages: 0,
            status: 'Pending',
            error: null,
            createdAt: '2026-07-30T12:00:00Z',
            completedAt: null,
          },
          { status: 202 },
        )
      }),
      http.get('/api/v1/contacts/share/share-1', () =>
        HttpResponse.json({
          id: 'share-1',
          senderNumberId: 'num-1',
          senderPhone: '5511900002222',
          destination: '5511777770000',
          totalContacts: 2,
          totalMessages: 1,
          sentMessages: 1,
          status: 'Completed',
          error: null,
          createdAt: '2026-07-30T12:00:00Z',
          completedAt: '2026-07-30T12:00:10Z',
        }),
      ),
    )

    renderWithProviders(<ContactsPage />)
    const user = userEvent.setup()
    await screen.findByTestId('contacts-table')

    await user.click(screen.getByRole('button', { name: 'Enviar por WhatsApp' }))
    // Número banido não pode ser remetente: só o ativo aparece na lista.
    const sender = screen.getByLabelText('Enviar pelo número')
    expect(within(sender).getAllByRole('option')).toHaveLength(1)

    await user.type(screen.getByLabelText('Enviar para'), '5511777770000')
    await user.click(screen.getByRole('button', { name: 'Enviar' }))

    await waitFor(() => expect(posted.body).toEqual({ senderNumberId: 'num-1', destination: '5511777770000' }))
    expect(posted.url!.searchParams.get('from')).toBeTruthy()
    expect(await screen.findByText(/Enviado: 2 contatos em 1 mensagem/)).toBeInTheDocument()
  })

  // Risco anti-ban não impede: o servidor devolve os avisos, a tela mostra e o
  // botão vira "Enviar mesmo assim", que repete o pedido com confirmRisk=true.
  it('mostra os avisos de risco e envia mesmo assim quando confirmado', async () => {
    const urls: URL[] = []
    mswServer.use(
      contactsHandler(),
      http.get('/api/v1/numbers', () =>
        HttpResponse.json([
          { id: 'num-1', phone: '5511900002222', status: 'Active', sellerId: 's1', sellerName: 'Ana' },
        ]),
      ),
      http.post('/api/v1/contacts/share', ({ request }) => {
        const url = new URL(request.url)
        urls.push(url)
        if (url.searchParams.get('confirmRisk') !== 'true') {
          return HttpResponse.json(
            {
              error: 'Enviar por este número agora tem risco de banimento.',
              requiresConfirmation: true,
              warnings: [
                { code: 'sendingPaused', message: 'O WhatsApp restringiu o envio por este número há pouco (erro 463).' },
                { code: 'outsideBusinessHours', message: 'Agora está fora do horário comercial.' },
              ],
            },
            { status: 409 },
          )
        }
        return HttpResponse.json(
          {
            id: 'share-1',
            senderNumberId: 'num-1',
            senderPhone: '5511900002222',
            destination: '5511777770000',
            totalContacts: 2,
            totalMessages: 1,
            sentMessages: 0,
            status: 'Pending',
            error: null,
            createdAt: '2026-07-30T12:00:00Z',
            completedAt: null,
          },
          { status: 202 },
        )
      }),
      http.get('/api/v1/contacts/share/share-1', () =>
        HttpResponse.json({
          id: 'share-1',
          senderNumberId: 'num-1',
          senderPhone: '5511900002222',
          destination: '5511777770000',
          totalContacts: 2,
          totalMessages: 1,
          sentMessages: 1,
          status: 'Completed',
          error: null,
          createdAt: '2026-07-30T12:00:00Z',
          completedAt: '2026-07-30T12:00:10Z',
        }),
      ),
    )

    renderWithProviders(<ContactsPage />)
    const user = userEvent.setup()
    await screen.findByTestId('contacts-table')

    await user.click(screen.getByRole('button', { name: 'Enviar por WhatsApp' }))
    await user.type(screen.getByLabelText('Enviar para'), '5511777770000')
    await user.click(screen.getByRole('button', { name: 'Enviar' }))

    const warnings = await screen.findByTestId('share-warnings')
    expect(warnings).toHaveTextContent('erro 463')
    expect(warnings).toHaveTextContent('fora do horário comercial')

    await user.click(screen.getByRole('button', { name: 'Enviar mesmo assim' }))

    expect(await screen.findByText(/Enviado: 2 contatos/)).toBeInTheDocument()
    expect(urls.map((u) => u.searchParams.get('confirmRisk'))).toEqual([null, 'true'])
  })

  // O link de exportação carrega exatamente os mesmos filtros da prévia.
  it('exporta com os mesmos filtros da tela', async () => {
    mswServer.use(contactsHandler())

    renderWithProviders(<ContactsPage />)
    const user = userEvent.setup()
    await screen.findByTestId('contacts-table')

    await user.click(screen.getByRole('button', { name: 'Sem desfecho' }))

    const href = screen.getByTestId('export-link').getAttribute('href') ?? ''
    const params = new URL(href, 'http://localhost').searchParams
    expect(params.get('outcomeTypes')).toBe('none')
    expect(params.get('from')).toBeTruthy()
    expect(params.get('to')).toBeTruthy()
  })

  describe('no celular', () => {
    // Nove colunas não cabem em 360px: cada contato vira um card com os mesmos campos.
    it('troca a tabela por cards', async () => {
      mswServer.use(contactsHandler())

      renderMobile(<ContactsPage />)

      const cards = await screen.findByTestId('contacts-cards')
      expect(screen.queryByTestId('contacts-table')).not.toBeInTheDocument()
      expect(within(cards).getByText('Maria Silva')).toBeInTheDocument()
      // Um card por contato, cada um com o número do cliente na prévia — já no
      // formato de leitura (+55 11 70000-1111).
      expect(within(cards).getAllByText('+55 11 70000-1111')).toHaveLength(2)

      // Vendedor e banimento ficam na parte escondida do card.
      expect(within(cards).queryByText('Bruno')).not.toBeInTheDocument()
      const user = userEvent.setup()
      await user.click(within(cards).getAllByRole('button', { name: /ver mais dados/ })[1])
      expect(within(cards).getByText('Bruno')).toBeInTheDocument()
    })

    // Os filtros saem da tela e vão para uma folha, com o número de filtros ativos no botão.
    it('abre os filtros numa folha e conta os ativos', async () => {
      mswServer.use(contactsHandler())

      renderMobile(<ContactsPage />)
      const user = userEvent.setup()
      await screen.findByTestId('contacts-cards')

      expect(screen.queryByTestId('filters')).not.toBeInTheDocument()

      await user.click(screen.getByRole('button', { name: 'Filtros' }))
      await user.click(within(screen.getByTestId('filters')).getByRole('button', { name: 'Sem desfecho' }))

      expect(screen.getByRole('button', { name: 'Filtros (1)' })).toBeInTheDocument()
      const href = screen.getByTestId('export-link').getAttribute('href') ?? ''
      expect(new URL(href, 'http://localhost').searchParams.get('outcomeTypes')).toBe('none')
    })
  })
})
