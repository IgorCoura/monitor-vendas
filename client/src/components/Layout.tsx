import { NavLink, Outlet } from 'react-router-dom'
import clsx from 'clsx'
import { BottomNav } from './mobile/BottomNav'

const links = [
  { to: '/', label: 'Dashboard' },
  { to: '/registry', label: 'Cadastros' },
  { to: '/contacts', label: 'Contatos' },
  { to: '/ai', label: 'Análises IA' },
  { to: '/labels', label: 'Etiquetas' },
  { to: '/proxies', label: 'Proxies' },
  { to: '/holidays', label: 'Feriados' },
]

export function Layout() {
  return (
    <div className="flex min-h-screen">
      {/* A sidebar de 224px é a razão de a UI de PC não caber no celular: em
          360px sobrava menos de 140px de conteúdo. Abaixo de `md` ela sai e a
          navegação vira a barra inferior. */}
      <aside className="hidden w-56 shrink-0 border-r border-edge bg-card px-4 py-6 md:block">
        <h1 className="mb-8 px-2 text-lg font-bold text-primary">
          Monitor de Vendas
        </h1>
        <nav className="flex flex-col gap-1">
          {links.map((link) => (
            <NavLink
              key={link.to}
              to={link.to}
              end={link.to === '/'}
              className={({ isActive }) =>
                clsx(
                  'rounded-lg px-3 py-2 text-sm font-medium transition-colors',
                  isActive
                    ? 'bg-primary-soft text-primary-strong'
                    : 'text-ink-muted hover:bg-surface hover:text-ink',
                )
              }
            >
              {link.label}
            </NavLink>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 border-b border-edge bg-card px-4 py-2.5 md:hidden">
          <h1 className="text-base font-bold text-primary">Monitor de Vendas</h1>
        </header>

        {/* pb-nav só existe abaixo de `md` (regra no index.css): no desktop o
            padding continua sendo exatamente o p-8 de antes. */}
        <main className="pb-nav min-w-0 flex-1 p-4 md:p-8">
          <Outlet />
        </main>
      </div>

      <BottomNav />
    </div>
  )
}
