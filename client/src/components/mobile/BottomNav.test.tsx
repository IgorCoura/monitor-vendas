import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { BottomNav } from './BottomNav'

function renderAt(route: string) {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <BottomNav />
    </MemoryRouter>,
  )
}

describe('BottomNav', () => {
  // Só as quatro rotas de uso diário ficam na barra: com todas elas, cada aba
  // caía para ~56px e os rótulos truncavam.
  it('mostra apenas as rotas principais na barra', () => {
    renderAt('/')

    const nav = screen.getByTestId('bottom-nav')
    const links = within(nav).getAllByRole('link')
    expect(links.map((l) => l.getAttribute('href'))).toEqual(['/', '/registry', '/contacts', '/ai'])
    expect(within(nav).getByRole('button', { name: 'Mais telas' })).toBeInTheDocument()
  })

  // As demais rotas vivem na folha do "⋯", que abre de baixo.
  it('abre as demais rotas na folha "Mais"', async () => {
    renderAt('/')
    const user = userEvent.setup()

    await user.click(screen.getByRole('button', { name: 'Mais telas' }))

    const sheet = await screen.findByTestId('more-sheet')
    expect(within(sheet).getByRole('button', { name: 'Proxies' })).toBeInTheDocument()
    expect(within(sheet).getByRole('button', { name: 'Etiquetas' })).toBeInTheDocument()
    expect(within(sheet).getByRole('button', { name: 'Feriados' })).toBeInTheDocument()
  })

  // O item da rota atual fica destacado — é o único indicador de "onde estou"
  // no celular, já que a sidebar não existe lá.
  it('destaca a rota atual', () => {
    renderAt('/contacts')

    expect(screen.getByRole('link', { name: 'Contatos' }).className).toContain('text-primary-strong')
    expect(screen.getByRole('link', { name: 'Painel' }).className).not.toContain('text-primary-strong')
  })

  // Estando numa rota do "⋯", é o botão Mais que acende: sem isso a barra
  // inteira ficaria apagada e o usuário não saberia onde está.
  it('acende o "Mais" quando a rota atual está dentro dele', () => {
    renderAt('/proxies')

    expect(screen.getByRole('button', { name: 'Mais telas' }).className).toContain('text-primary-strong')
  })

  // Dashboard só fica ativo na raiz (end): sem isso ele acenderia em toda rota.
  it('não marca o Painel como ativo fora da raiz', () => {
    renderAt('/holidays')

    expect(screen.getByRole('link', { name: 'Painel' }).className).toContain('text-ink-muted')
  })
})
