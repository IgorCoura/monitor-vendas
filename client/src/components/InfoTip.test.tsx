import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { InfoTip } from './ui'

describe('InfoTip', () => {
  // O balão só aparece no hover e some quando o mouse sai.
  it('mostra o texto no hover e esconde ao sair', async () => {
    render(<InfoTip text="Explicação da métrica de teste." />)
    const user = userEvent.setup()
    const icon = screen.getByRole('img', { name: 'Explicação da métrica de teste.' })

    expect(screen.queryByText('Explicação da métrica de teste.')).not.toBeInTheDocument()

    await user.hover(icon)
    const tip = screen.getByText('Explicação da métrica de teste.')
    // Posição fixa clampada à viewport (nunca negativa) e largura definida:
    // o texto longe da borda não é cortado e quebra linha dentro da caixa.
    expect(tip).toHaveClass('fixed')
    expect(tip).toHaveStyle({ width: '256px' })
    expect(Number.parseFloat(tip.style.left)).toBeGreaterThanOrEqual(8)

    await user.unhover(icon)
    expect(screen.queryByText('Explicação da métrica de teste.')).not.toBeInTheDocument()
  })
})
