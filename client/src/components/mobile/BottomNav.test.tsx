import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
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
  // A barra inferior leva às mesmas rotas da sidebar do desktop, na mesma ordem.
  it('lista as rotas do app', () => {
    renderAt('/')

    const nav = screen.getByTestId('bottom-nav')
    const links = within(nav).getAllByRole('link')
    expect(links.map((l) => l.getAttribute('href'))).toEqual([
      '/',
      '/registry',
      '/contacts',
      '/ai',
      '/labels',
      '/holidays',
    ])
  })

  // O item da rota atual fica destacado — é o único indicador de "onde estou"
  // no celular, já que a sidebar não existe lá.
  it('destaca a rota atual', () => {
    renderAt('/contacts')

    expect(screen.getByRole('link', { name: 'Contatos' }).className).toContain('text-primary-strong')
    expect(screen.getByRole('link', { name: 'Painel' }).className).not.toContain('text-primary-strong')
  })

  // Dashboard só fica ativo na raiz (end): sem isso ele acenderia em toda rota.
  it('não marca o Painel como ativo fora da raiz', () => {
    renderAt('/holidays')

    expect(screen.getByRole('link', { name: 'Painel' }).className).toContain('text-ink-muted')
  })
})
