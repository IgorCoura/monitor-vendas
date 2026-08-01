import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { Dialog, Input } from './ui'

function Host({ onClose }: { onClose: () => void }) {
  const [value, setValue] = useState('')
  return (
    <Dialog open onClose={onClose} title="Teste" footer={<button type="button">Confirmar</button>}>
      <Input aria-label="Campo" value={value} onChange={(e) => setValue(e.target.value)} />
    </Dialog>
  )
}

describe('Dialog', () => {
  // Escape fecha: no celular não há canto de tela sobrando para clicar fora.
  it('fecha com a tecla Escape', async () => {
    const onClose = vi.fn()
    render(<Host onClose={onClose} />)
    const user = userEvent.setup()

    await user.keyboard('{Escape}')

    expect(onClose).toHaveBeenCalledTimes(1)
  })

  // Com o dialog aberto o fundo não pode rolar; ao fechar, a rolagem volta.
  it('trava a rolagem do fundo enquanto está aberto', () => {
    const { unmount } = render(<Host onClose={() => {}} />)
    expect(document.body.style.overflow).toBe('hidden')

    unmount()
    expect(document.body.style.overflow).toBe('')
  })

  // Regressão: o `onClose` é uma arrow nova a cada render, e o efeito do dialog
  // chegou a reexecutar (e refocar o painel) a cada tecla, engolindo o texto.
  it('não rouba o foco do campo enquanto se digita', async () => {
    render(<Host onClose={() => {}} />)
    const user = userEvent.setup()

    const field = screen.getByLabelText('Campo')
    await user.type(field, '5511999999999')

    expect(field).toHaveValue('5511999999999')
    expect(field).toHaveFocus()
  })

  // O rodapé fica fora da área rolável: é o que mantém o botão de ação
  // alcançável quando o corpo do dialog é maior que a tela.
  it('mantém o rodapé fora do corpo rolável', () => {
    render(<Host onClose={() => {}} />)

    const body = screen.getByTestId('dialog-body')
    const footer = screen.getByTestId('dialog-footer')
    expect(body).not.toContainElement(footer)
    expect(footer).toHaveTextContent('Confirmar')
  })

  // O dialog monta no body, não onde é chamado: `fixed` e `z-index` só valem
  // dentro do stacking context mais próximo, e `opacity`/`transform` em qualquer
  // ancestral cria um. (Regressão: aberto de dentro do card de vendedor inativo
  // — que usa `opacity-60` —, o QR aparecia meio transparente e atrás dos
  // outros cards.)
  it('monta fora do container que o chamou', () => {
    const { container } = render(
      <div className="opacity-60">
        <Host onClose={() => {}} />
      </div>,
    )

    const dialog = screen.getByRole('dialog')
    expect(container).not.toContainElement(dialog)
    expect(document.body).toContainElement(dialog)
  })
})
