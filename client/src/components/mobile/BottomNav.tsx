import { NavLink } from 'react-router-dom'
import clsx from 'clsx'
import type { ReactNode } from 'react'

// Barra de navegação do celular: as mesmas rotas da sidebar, fixas no rodapé,
// onde o polegar alcança. Ícone + rótulo curto — só ícone deixaria "Etiquetas"
// e "Feriados" indistinguíveis.
function DashboardIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <rect x="2.5" y="9" width="3.5" height="8.5" rx="1" />
      <rect x="8.25" y="4.5" width="3.5" height="13" rx="1" />
      <rect x="14" y="11.5" width="3.5" height="6" rx="1" />
    </svg>
  )
}

function RegistryIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M7 3.5a3 3 0 1 1 0 6 3 3 0 0 1 0-6Zm0 7.25c3.04 0 5.5 1.6 5.5 3.58v1.17a1 1 0 0 1-1 1H2.5a1 1 0 0 1-1-1v-1.17c0-1.98 2.46-3.58 5.5-3.58Z" />
      <path d="M14 6.5h4a.75.75 0 0 1 0 1.5h-4a.75.75 0 0 1 0-1.5Zm0 3.25h4a.75.75 0 0 1 0 1.5h-4a.75.75 0 0 1 0-1.5Z" />
    </svg>
  )
}

function ContactsIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M4 2.5h12a1.5 1.5 0 0 1 1.5 1.5v12a1.5 1.5 0 0 1-1.5 1.5H4A1.5 1.5 0 0 1 2.5 16V4A1.5 1.5 0 0 1 4 2.5Zm6 3a2.25 2.25 0 1 0 0 4.5 2.25 2.25 0 0 0 0-4.5Zm0 5.5c-2.2 0-4 1.16-4 2.6v.65h8v-.65c0-1.44-1.8-2.6-4-2.6Z" />
    </svg>
  )
}

function AiIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M10 1.5l1.35 3.9 3.9 1.35-3.9 1.35L10 12l-1.35-3.9-3.9-1.35 3.9-1.35L10 1.5Zm5.25 9.5l.72 2.08 2.08.72-2.08.72-.72 2.08-.72-2.08-2.08-.72 2.08-.72.72-2.08Zm-10 1.5l.54 1.56 1.56.54-1.56.54-.54 1.56-.54-1.56-1.56-.54 1.56-.54.54-1.56Z" />
    </svg>
  )
}

function LabelsIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M9.6 2.5H16a1.5 1.5 0 0 1 1.5 1.5v6.4a1.5 1.5 0 0 1-.44 1.06l-5.6 5.6a1.5 1.5 0 0 1-2.12 0l-6.4-6.4a1.5 1.5 0 0 1 0-2.12l5.6-5.6A1.5 1.5 0 0 1 9.6 2.5Zm3.65 2.75a1.25 1.25 0 1 0 0 2.5 1.25 1.25 0 0 0 0-2.5Z" />
    </svg>
  )
}

function HolidaysIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M6 1.75c.41 0 .75.34.75.75v1h6.5v-1a.75.75 0 0 1 1.5 0v1H16A1.5 1.5 0 0 1 17.5 5v11a1.5 1.5 0 0 1-1.5 1.5H4A1.5 1.5 0 0 1 2.5 16V5A1.5 1.5 0 0 1 4 3.5h1.25v-1c0-.41.34-.75.75-.75ZM4 7.5V16h12V7.5H4Z" />
    </svg>
  )
}

function ProxiesIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M10 1.75a8.25 8.25 0 1 0 0 16.5 8.25 8.25 0 0 0 0-16.5ZM3.3 10c0-.7.11-1.36.31-1.99h2.2a15.6 15.6 0 0 0 0 3.98h-2.2A6.7 6.7 0 0 1 3.3 10Zm4.02 0c0-.69.05-1.35.13-1.99h5.1a15.6 15.6 0 0 1 0 3.98h-5.1A15.6 15.6 0 0 1 7.32 10Zm6.87-1.99h2.2a6.73 6.73 0 0 1 0 3.98h-2.2a15.6 15.6 0 0 0 0-3.98ZM10 3.3c.74 0 1.7 1.13 2.2 3.21H7.8C8.3 4.43 9.26 3.3 10 3.3Zm0 13.4c-.74 0-1.7-1.13-2.2-3.21h4.4c-.5 2.08-1.46 3.21-2.2 3.21Z" />
    </svg>
  )
}

// Os rótulos são mais curtos que os da sidebar ("Painel", "IA"). Com SETE abas
// em 390px sobram ~56px para cada uma, e um rótulo de 9 caracteres passa disso:
// o `truncate` no rótulo faz o excesso virar reticências em vez de quebrar em
// duas linhas e desalinhar a altura da barra inteira.
const items: { to: string; label: string; icon: ReactNode }[] = [
  { to: '/', label: 'Painel', icon: <DashboardIcon /> },
  { to: '/registry', label: 'Cadastros', icon: <RegistryIcon /> },
  { to: '/contacts', label: 'Contatos', icon: <ContactsIcon /> },
  { to: '/ai', label: 'IA', icon: <AiIcon /> },
  { to: '/labels', label: 'Etiquetas', icon: <LabelsIcon /> },
  { to: '/proxies', label: 'Proxies', icon: <ProxiesIcon /> },
  { to: '/holidays', label: 'Feriados', icon: <HolidaysIcon /> },
]

export function BottomNav() {
  return (
    <nav
      aria-label="Navegação principal"
      data-testid="bottom-nav"
      className="pb-inset fixed inset-x-0 bottom-0 z-40 flex border-t border-edge bg-card md:hidden"
    >
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.to === '/'}
          className={({ isActive }) =>
            clsx(
              'flex min-w-0 flex-1 flex-col items-center justify-center gap-0.5 px-0.5 py-2 text-center text-[11px] font-medium transition-colors',
              isActive ? 'text-primary-strong' : 'text-ink-muted',
            )
          }
        >
          {item.icon}
          <span className="w-full truncate">{item.label}</span>
        </NavLink>
      ))}
    </nav>
  )
}
