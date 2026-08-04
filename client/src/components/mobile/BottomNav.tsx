import { useState } from 'react'
import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import clsx from 'clsx'
import { overflowRoutes, primaryRoutes } from '../navigation'
import { Dialog } from '../ui'

function MoreIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <circle cx="4.5" cy="10" r="1.6" />
      <circle cx="10" cy="10" r="1.6" />
      <circle cx="15.5" cy="10" r="1.6" />
    </svg>
  )
}

// Barra de navegação do celular: as rotas de uso diário no rodapé, onde o
// polegar alcança, e as de configuração atrás do "⋯". Ícone + rótulo curto — só
// ícone deixaria "Etiquetas" e "Feriados" indistinguíveis.
export function BottomNav() {
  const [moreOpen, setMoreOpen] = useState(false)
  const { pathname } = useLocation()
  const navigate = useNavigate()

  // O "⋯" acende quando a rota atual está DENTRO dele: sem isso, quem está em
  // Proxies vê a barra inteira apagada e não sabe onde está.
  const inOverflow = overflowRoutes.some((r) => pathname.startsWith(r.to))

  return (
    <>
      <nav
        aria-label="Navegação principal"
        data-testid="bottom-nav"
        className="pb-inset fixed inset-x-0 bottom-0 z-40 flex border-t border-edge bg-card md:hidden"
      >
        {primaryRoutes.map((item) => (
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
            {/* Rede de segurança para telas de 320px: com quatro abas os rótulos
                cabem, mas truncar é melhor que quebrar a altura da barra. */}
            <span className="w-full truncate">{item.mobileLabel}</span>
          </NavLink>
        ))}

        <button
          type="button"
          aria-label="Mais telas"
          aria-expanded={moreOpen}
          onClick={() => setMoreOpen(true)}
          className={clsx(
            'flex min-w-0 flex-1 flex-col items-center justify-center gap-0.5 px-0.5 py-2 text-center text-[11px] font-medium transition-colors',
            inOverflow ? 'text-primary-strong' : 'text-ink-muted',
          )}
        >
          <MoreIcon />
          <span className="w-full truncate">Mais</span>
        </button>
      </nav>

      {/* Folha de baixo, não popover: o Menu do projeto abre um dropdown de
          208px preso ao botão, apertado para quatro itens e estranho colado no
          rodapé. O Dialog já vira bottom sheet abaixo de `md`. */}
      <Dialog open={moreOpen} onClose={() => setMoreOpen(false)} title="Mais telas">
        <ul className="space-y-1" data-testid="more-sheet">
          {overflowRoutes.map((item) => (
            <li key={item.to}>
              <button
                type="button"
                onClick={() => {
                  setMoreOpen(false)
                  navigate(item.to)
                }}
                className={clsx(
                  'flex min-h-11 w-full items-center gap-3 rounded-lg px-3 text-left text-sm font-medium transition-colors',
                  pathname.startsWith(item.to)
                    ? 'bg-primary-soft text-primary-strong'
                    : 'text-ink hover:bg-surface',
                )}
              >
                {item.icon}
                {item.mobileLabel}
              </button>
            </li>
          ))}
        </ul>
      </Dialog>
    </>
  )
}
