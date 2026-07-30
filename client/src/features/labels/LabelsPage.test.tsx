import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { LabelsPage } from './LabelsPage'
import { renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer } from '../../test/msw'

describe('LabelsPage', () => {
  // Cada tipo aparece com suas etiquetas aceitas.
  it('lista os tipos e suas etiquetas', async () => {
    renderWithProviders(<LabelsPage />)

    expect(await screen.findByText('Vendas')).toBeInTheDocument()
    expect(screen.getByText('Clientes perdidos')).toBeInTheDocument()
    expect(within(screen.getByTestId('type-sale')).getByText('venda')).toBeInTheDocument()
    expect(
      within(screen.getByTestId('type-lost')).getByText(/Nenhuma etiqueta ainda/),
    ).toBeInTheDocument()
  })

  // Adicionar etiqueta a um tipo envia o termo para a API daquele tipo.
  it('adiciona etiqueta ao tipo', async () => {
    let posted: { code?: string; term?: string } = {}
    mswServer.use(
      http.post('/api/v1/outcome-types/:code/terms', async ({ params, request }) => {
        const body = (await request.json()) as { term: string }
        posted = { code: String(params.code), term: body.term }
        return HttpResponse.json({ id: 'novo', term: body.term }, { status: 201 })
      }),
    )

    renderWithProviders(<LabelsPage />)
    const user = userEvent.setup()

    const lostCard = await screen.findByTestId('type-lost')
    await user.type(within(lostCard).getByLabelText('Nova etiqueta para Clientes perdidos'), 'desistiu')
    await user.click(within(lostCard).getByRole('button', { name: 'Adicionar' }))

    await waitFor(() => expect(posted).toEqual({ code: 'lost', term: 'desistiu' }))
  })

  // Etiqueta já usada em outro tipo mostra o erro devolvido pela API.
  it('mostra erro de etiqueta duplicada', async () => {
    mswServer.use(
      http.post('/api/v1/outcome-types/:code/terms', () =>
        HttpResponse.json({ error: "Esta etiqueta já está no tipo 'sale'." }, { status: 409 }),
      ),
    )

    renderWithProviders(<LabelsPage />)
    const user = userEvent.setup()

    const lostCard = await screen.findByTestId('type-lost')
    await user.type(within(lostCard).getByLabelText('Nova etiqueta para Clientes perdidos'), 'venda')
    await user.click(within(lostCard).getByRole('button', { name: 'Adicionar' }))

    expect(await screen.findByText("Esta etiqueta já está no tipo 'sale'.")).toBeInTheDocument()
  })

  // Sugestões do WhatsApp mostram a etiqueta real e permitem atribuí-la a um tipo.
  it('sugere etiquetas encontradas no WhatsApp', async () => {
    let posted: { code?: string; term?: string } = {}
    mswServer.use(
      http.get('/api/v1/outcome-labels/suggestions', () =>
        HttpResponse.json([
          { labelId: 'lbl-9', name: 'Fechado ✅', conversations: 12, mappedToTypeCode: null },
          { labelId: 'lbl-1', name: 'venda', conversations: 3, mappedToTypeCode: 'sale' },
        ]),
      ),
      http.post('/api/v1/outcome-types/:code/terms', async ({ params, request }) => {
        const body = (await request.json()) as { term: string }
        posted = { code: String(params.code), term: body.term }
        return HttpResponse.json({ id: 'novo', term: body.term }, { status: 201 })
      }),
    )

    renderWithProviders(<LabelsPage />)
    const user = userEvent.setup()

    const suggestions = await screen.findByTestId('suggestions')
    expect(await within(suggestions).findByText('Fechado ✅')).toBeInTheDocument()
    expect(within(suggestions).getByText('12 conversas')).toBeInTheDocument()
    // Etiqueta já mapeada não aparece como pendente.
    expect(within(suggestions).queryByText('venda')).not.toBeInTheDocument()

    await user.click(within(suggestions).getByRole('button', { name: '→ Vendas' }))

    await waitFor(() => expect(posted).toEqual({ code: 'sale', term: 'Fechado ✅' }))
  })
})
