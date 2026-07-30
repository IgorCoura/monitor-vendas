import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HolidaysPage } from './HolidaysPage'
import { renderWithProviders } from '../../test/render'
import { http, HttpResponse, mswServer } from '../../test/msw'

describe('HolidaysPage', () => {
  // Lista os feriados cadastrados formatando a data em pt-BR.
  it('lista feriados existentes', async () => {
    mswServer.use(
      http.get('/api/v1/holidays', () =>
        HttpResponse.json([{ id: crypto.randomUUID(), date: '2026-09-07', name: 'Independência' }]),
      ),
    )

    renderWithProviders(<HolidaysPage />)

    expect(await screen.findByText('07/09/2026')).toBeInTheDocument()
    expect(screen.getByText('Independência')).toBeInTheDocument()
  })

  // Data duplicada (409) mostra a mensagem de erro da API abaixo do formulário.
  it('mostra o erro de data duplicada', async () => {
    mswServer.use(
      http.post('/api/v1/holidays', () =>
        HttpResponse.json({ error: 'Já existe feriado cadastrado nesta data.' }, { status: 409 }),
      ),
    )

    renderWithProviders(<HolidaysPage />)
    const user = userEvent.setup()

    await user.type(screen.getByLabelText('Data do feriado'), '2026-12-25')
    await user.type(screen.getByLabelText('Nome do feriado'), 'Natal')
    await user.click(screen.getByRole('button', { name: 'Adicionar feriado' }))

    expect(await screen.findByText('Já existe feriado cadastrado nesta data.')).toBeInTheDocument()
  })
})
