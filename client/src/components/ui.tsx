import {
  useState,
  type ReactNode,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type InputHTMLAttributes,
  type MouseEvent as ReactMouseEvent,
} from 'react'
import clsx from 'clsx'
import type { NumberStatus } from '../api/types'

export function Card({ children, className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={clsx('rounded-xl border border-edge bg-card p-5 shadow-sm', className)} {...rest}>
      {children}
    </div>
  )
}

type ButtonVariant = 'primary' | 'ghost' | 'danger'

export function Button({
  variant = 'primary',
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return (
    <button
      className={clsx(
        'rounded-lg px-3 py-1.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50',
        variant === 'primary' && 'bg-primary text-white hover:bg-primary-strong',
        variant === 'ghost' && 'border border-edge bg-card text-ink hover:bg-surface',
        variant === 'danger' && 'bg-danger-soft text-danger hover:bg-danger hover:text-white',
        className,
      )}
      {...props}
    />
  )
}

export function Input(props: InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      {...props}
      className={clsx(
        'rounded-lg border border-edge bg-card px-3 py-1.5 text-sm text-ink placeholder:text-ink-muted focus:border-primary focus:outline-none',
        props.className,
      )}
    />
  )
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
  if (!open) return null
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-ink/40 p-4"
      onClick={onClose}
      role="presentation"
    >
      <div
        role="dialog"
        aria-label={title}
        className={clsx(
          'flex max-h-[85vh] w-full flex-col rounded-xl border border-edge bg-card shadow-lg',
          size === 'lg' ? 'max-w-2xl' : 'max-w-md',
        )}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-6 pt-6 pb-4">
          <h2 className="text-base font-semibold">{title}</h2>
          <Button variant="ghost" onClick={onClose} aria-label="Fechar">
            ✕
          </Button>
        </div>
        <div data-testid="dialog-body" className="flex-1 overflow-y-auto px-6 pb-6">
          {children}
        </div>
        {/* Fora da área rolável de propósito: dentro dela o rodapé grudado cobre
            o último campo do formulário e rouba o clique dele. */}
        {footer && (
          <div data-testid="dialog-footer" className="border-t border-edge px-6 py-3">
            {footer}
          </div>
        )}
      </div>
    </div>
  )
}

// "?" discreto que mostra a explicação da métrica no hover. O balão usa
// position:fixed com a coordenada clampada à viewport: perto da borda direita
// ele desloca para a esquerda em vez de ser cortado, e escapa de containers
// com overflow (ex.: a tabela com scroll horizontal).
const TOOLTIP_WIDTH = 256

export function InfoTip({ text }: { text: string }) {
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)

  function show(e: ReactMouseEvent<HTMLSpanElement>) {
    const rect = e.currentTarget.getBoundingClientRect()
    const margin = 8
    const x = Math.min(
      Math.max(rect.left + rect.width / 2 - TOOLTIP_WIDTH / 2, margin),
      window.innerWidth - TOOLTIP_WIDTH - margin,
    )
    setPos({ x, y: rect.bottom + 6 })
  }

  return (
    <span className="inline-flex align-middle">
      <span
        role="img"
        aria-label={text}
        onMouseEnter={show}
        onMouseLeave={() => setPos(null)}
        className="flex h-3.5 w-3.5 cursor-help items-center justify-center rounded-full border border-edge text-[9px] leading-none text-ink-muted"
      >
        ?
      </span>
      {pos && (
        <span
          style={{ left: pos.x, top: pos.y, width: TOOLTIP_WIDTH }}
          className="pointer-events-none fixed z-40 rounded-lg border border-edge bg-card p-2.5 text-left text-xs font-normal tracking-normal break-words whitespace-normal normal-case shadow-md"
        >
          {text}
        </span>
      )}
    </span>
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
