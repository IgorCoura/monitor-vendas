import {
  useEffect,
  useRef,
  useState,
  type ReactNode,
  type ButtonHTMLAttributes,
  type Ref,
  type HTMLAttributes,
  type InputHTMLAttributes,
  type SelectHTMLAttributes,
} from 'react'
import { createPortal } from 'react-dom'
import clsx from 'clsx'
import type { NumberStatus } from '../api/types'
import { useIsMobile } from '../lib/useIsMobile'

export function Card({ children, className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={clsx('rounded-xl border border-edge bg-card p-4 shadow-sm md:p-5', className)}
      {...rest}
    >
      {children}
    </div>
  )
}

type ButtonVariant = 'primary' | 'ghost' | 'danger'

// `min-h-11` (44px) só abaixo do `md`: é o alvo mínimo de toque. No desktop o
// botão continua com a altura compacta de sempre.
// `ref` como prop normal (React 19): o Menu precisa medir o botão para se
// posicionar.
export function Button({
  variant = 'primary',
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant
  ref?: Ref<HTMLButtonElement>
}) {
  return (
    <button
      className={clsx(
        'inline-flex min-h-11 items-center justify-center rounded-lg px-3 py-1.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 md:min-h-0',
        variant === 'primary' && 'bg-primary text-white hover:bg-primary-strong',
        variant === 'ghost' && 'border border-edge bg-card text-ink hover:bg-surface',
        variant === 'danger' && 'bg-danger-soft text-danger hover:bg-danger hover:text-white',
        className,
      )}
      {...props}
    />
  )
}

const fieldClass =
  'min-h-11 rounded-lg border border-edge bg-card px-3 py-1.5 text-base text-ink placeholder:text-ink-muted focus:border-primary focus:outline-none md:min-h-0 md:text-sm'

export function Input(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={clsx(fieldClass, props.className)} />
}

// Mesma moldura do Input: os <select> viviam com a classe repetida à mão em
// cada página, e nenhum deles tinha alvo de toque nem fonte de 16px.
export function Select(props: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select {...props} className={clsx(fieldClass, props.className)} />
}

const statusStyle: Record<NumberStatus, { label: string; className: string }> = {
  Active: { label: 'Ativo', className: 'bg-ok-soft text-ok' },
  Disconnected: { label: 'Desconectado', className: 'bg-warn-soft text-warn' },
  BannedTemporary: { label: 'Ban temporário', className: 'bg-danger-soft text-danger' },
  BannedPermanent: { label: 'Ban permanente', className: 'bg-danger text-white' },
}

export function StatusBadge({ status }: { status: NumberStatus }) {
  const style = statusStyle[status] ?? statusStyle.Disconnected
  return (
    <span className={clsx('rounded-full px-2.5 py-0.5 text-xs font-semibold', style.className)}>
      {style.label}
    </span>
  )
}

// O conteúdo pode crescer muito (o dialog de exportação tem dezenas de chips):
// a altura é limitada à viewport e só o corpo rola. Sem isso o dialog cresce
// além da tela e leva os botões de ação junto, para fora do alcance.
//
// No celular vira folha inferior (bottom sheet) colada na borda de baixo, onde
// o polegar alcança. A altura usa `dvh`, não `vh`: no mobile o `vh` é medido com
// a barra de URL escondida, então um dialog de 85vh nasce mais alto que a área
// visível e empurra o rodapé (Gerar planilha, Enviar) para fora da tela.
export function Dialog({
  open,
  onClose,
  title,
  size = 'md',
  footer,
  children,
}: {
  open: boolean
  onClose: () => void
  title: string
  size?: 'md' | 'lg'
  footer?: ReactNode
  children: ReactNode
}) {
  const panelRef = useRef<HTMLDivElement>(null)

  // O `onClose` que as telas passam é uma arrow nova a cada render. Guardá-lo
  // num ref mantém o efeito abaixo dependente só de `open`: sem isso ele
  // reexecutava a cada tecla digitada e o `focus()` roubava o cursor do campo.
  const closeRef = useRef(onClose)
  closeRef.current = onClose

  // Escape fecha; e o fundo trava enquanto o dialog está aberto — sem isso o
  // arrasto sobre a folha rola a página atrás dela no celular.
  useEffect(() => {
    if (!open) return

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') closeRef.current()
    }

    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', onKeyDown)
    panelRef.current?.focus()

    return () => {
      document.body.style.overflow = previousOverflow
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  if (!open) return null

  // Portal no body porque `fixed` e `z-index` só valem dentro do stacking
  // context mais próximo — e qualquer ancestral com `opacity`, `transform` ou
  // `filter` cria um. Renderizado onde é chamado, o dialog aberto de dentro do
  // card de um vendedor inativo (`opacity-60`) herdava a transparência e ficava
  // preso atrás dos outros cards.
  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-end justify-center bg-ink/40 p-0 md:items-center md:p-4"
      onClick={onClose}
      role="presentation"
    >
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        tabIndex={-1}
        className={clsx(
          'flex max-h-[90dvh] w-full flex-col rounded-t-2xl border border-edge bg-card shadow-lg focus:outline-none md:max-h-[85vh] md:rounded-xl',
          size === 'lg' ? 'md:max-w-2xl' : 'md:max-w-md',
        )}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between gap-2 px-4 pt-4 pb-3 md:px-6 md:pt-6 md:pb-4">
          <h2 className="min-w-0 text-base font-semibold">{title}</h2>
          <Button variant="ghost" onClick={onClose} aria-label="Fechar" className="shrink-0">
            ✕
          </Button>
        </div>
        <div
          data-testid="dialog-body"
          className="flex-1 overflow-y-auto overscroll-contain px-4 pb-4 md:px-6 md:pb-6"
        >
          {children}
        </div>
        {/* Fora da área rolável de propósito: dentro dela o rodapé grudado cobre
            o último campo do formulário e rouba o clique dele. */}
        {footer && (
          <div
            data-testid="dialog-footer"
            className="pb-safe border-t border-edge px-4 py-3 md:px-6"
          >
            {footer}
          </div>
        )}
      </div>
    </div>,
    document.body,
  )
}

// "?" discreto que mostra a explicação da métrica. O balão usa position:fixed
// com a coordenada clampada à viewport: perto da borda direita ele desloca para
// a esquerda em vez de ser cortado, e escapa de containers com overflow (ex.: a
// tabela com scroll horizontal).
//
// No desktop abre no hover; no celular **abre no toque** e fecha no toque
// seguinte, tocando fora ou rolando a página — sem isso a ajuda de toda métrica
// (a convenção do projeto) simplesmente não existiria no telefone.
const TOOLTIP_MAX_WIDTH = 256

export function InfoTip({ text }: { text: string }) {
  const isMobile = useIsMobile()
  const [pos, setPos] = useState<{ x: number; y: number; width: number } | null>(null)
  const anchorRef = useRef<HTMLSpanElement>(null)

  function show() {
    const rect = anchorRef.current?.getBoundingClientRect()
    if (!rect) return
    const margin = 8
    const width = Math.min(TOOLTIP_MAX_WIDTH, window.innerWidth - margin * 2)
    const x = Math.min(
      Math.max(rect.left + rect.width / 2 - width / 2, margin),
      window.innerWidth - width - margin,
    )
    setPos({ x, y: rect.bottom + 6, width })
  }

  const hide = () => setPos(null)

  // Aberto no toque, o balão precisa sumir quando o dedo vai para outro lugar:
  // qualquer rolagem ou toque fora fecha. O toque no próprio "?" é ignorado
  // aqui — quem cuida dele é o onClick, senão fechar e reabrir no mesmo toque
  // deixaria o balão preso aberto.
  useEffect(() => {
    if (!pos || !isMobile) return

    function onPointerDown(event: PointerEvent) {
      if (anchorRef.current?.contains(event.target as Node)) return
      hide()
    }

    document.addEventListener('pointerdown', onPointerDown)
    window.addEventListener('scroll', hide, true)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      window.removeEventListener('scroll', hide, true)
    }
  }, [pos, isMobile])

  return (
    <span className="inline-flex align-middle">
      <span
        ref={anchorRef}
        role="img"
        aria-label={text}
        onMouseEnter={isMobile ? undefined : show}
        onMouseLeave={isMobile ? undefined : hide}
        onClick={
          isMobile
            ? (e) => {
                e.stopPropagation()
                if (pos) hide()
                else show()
              }
            : undefined
        }
        className={clsx(
          'flex items-center justify-center rounded-full border border-edge leading-none text-ink-muted',
          isMobile
            ? 'h-5 w-5 shrink-0 cursor-pointer text-[11px]'
            : 'h-3.5 w-3.5 cursor-help text-[9px]',
        )}
      >
        ?
      </span>
      {pos && (
        <span
          role="tooltip"
          style={{ left: pos.x, top: pos.y, width: pos.width }}
          className="pointer-events-none fixed z-40 rounded-lg border border-edge bg-card p-2.5 text-left text-xs font-normal tracking-normal break-words whitespace-normal normal-case shadow-md"
        >
          {text}
        </span>
      )}
    </span>
  )
}

export type MenuAction = { label: string; onSelect: () => void; danger?: boolean }

// Ações raras e destrutivas saem da linha e entram aqui: com todas visíveis, a
// linha do número virava um bloco de cinco botões que empurrava a lista inteira
// no celular. Posicionado com `fixed` a partir do botão (como o InfoTip), porque
// dentro da linha ele seria cortado pelo overflow dela.
export function Menu({ label, actions }: { label: string; actions: MenuAction[] }) {
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const anchorRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)

  const close = () => setPos(null)

  function toggle() {
    if (pos) return close()
    const rect = anchorRef.current?.getBoundingClientRect()
    if (!rect) return
    const width = 208
    const margin = 8
    setPos({
      x: Math.min(Math.max(rect.right - width, margin), window.innerWidth - width - margin),
      y: rect.bottom + 4,
    })
  }

  // Clicar fora, rolar ou apertar Escape fecha — menu preso aberto cobre a linha
  // de baixo e o próximo clique vai para a ação errada.
  useEffect(() => {
    if (!pos) return

    // O clique DENTRO do menu não fecha aqui: fechar no `pointerdown` desmonta o
    // item antes de o `click` chegar nele, e a ação nunca dispara.
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node
      if (anchorRef.current?.contains(target) || menuRef.current?.contains(target)) return
      close()
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') close()
    }

    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    window.addEventListener('scroll', close, true)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
      window.removeEventListener('scroll', close, true)
    }
  }, [pos])

  return (
    <>
      <Button ref={anchorRef} variant="ghost" aria-label={label} aria-expanded={pos !== null} onClick={toggle}>
        ⋯
      </Button>
      {pos &&
        createPortal(
          <div
            ref={menuRef}
            role="menu"
            aria-label={label}
            style={{ left: pos.x, top: pos.y, width: 208 }}
            className="fixed z-50 overflow-hidden rounded-lg border border-edge bg-card py-1 shadow-md"
          >
            {actions.map((action) => (
              <button
                key={action.label}
                type="button"
                role="menuitem"
                onClick={() => {
                  close()
                  action.onSelect()
                }}
                className={clsx(
                  'block w-full px-3 py-2 text-left text-sm hover:bg-surface',
                  action.danger && 'text-danger',
                )}
              >
                {action.label}
              </button>
            ))}
          </div>,
          document.body,
        )}
    </>
  )
}

export function Spinner() {
  return <p className="py-8 text-center text-sm text-ink-muted">Carregando…</p>
}

export function ErrorState({ message }: { message: string }) {
  return (
    <Card className="border-danger-soft">
      <p className="text-sm text-danger">{message}</p>
    </Card>
  )
}

export function EmptyState({ message }: { message: string }) {
  return <p className="py-8 text-center text-sm text-ink-muted">{message}</p>
}
