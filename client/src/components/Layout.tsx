import { NavLink, Outlet } from 'react-router-dom'
import clsx from 'clsx'

const links = [
  { to: '/', label: 'Dashboard' },
  { to: '/registry', label: 'Cadastros' },
  { to: '/contacts', label: 'Contatos' },
  { to: '/ai', label: 'Análises IA' },
  { to: '/labels', label: 'Etiquetas' },
  { to: '/holidays', label: 'Feriados' },
]

export function Layout() {
  return (
    <div className="flex min-h-screen">
      <aside className="w-56 shrink-0 border-r border-edge bg-card px-4 py-6">
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
      <main className="min-w-0 flex-1 p-6 md:p-8">
        <Outlet />
      </main>
    </div>
  )
}
