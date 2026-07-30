import type { ReactElement } from 'react'
import { render } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { setMobileViewport } from './viewport'

// Renderiza com QueryClient sem retry (falha aparece na hora) e router em memória.
export function renderWithProviders(ui: ReactElement, { route = '/' } = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchInterval: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
    </QueryClientProvider>,
  )
}

// Mesmo render, mas com a media query de celular ligada ANTES de montar — é
// assim que useIsMobile passa a devolver true. O setup.ts devolve para desktop
// depois de cada teste.
export function renderMobile(ui: ReactElement, options: { route?: string } = {}) {
  setMobileViewport(true)
  return renderWithProviders(ui, options)
}
