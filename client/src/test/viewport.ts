import { MOBILE_QUERY } from '../lib/useIsMobile'

// jsdom não implementa matchMedia, e useIsMobile (usado por ui.tsx, Layout e
// pelas páginas) depende dele: sem este stub a suíte inteira quebra ao
// renderizar qualquer tela. O default é **desktop**, então todo teste escrito
// antes do mobile continua valendo sem alteração.
let mobile = false
const listeners = new Set<() => void>()

export function installMatchMedia(): void {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: (query: string) => ({
      media: query,
      matches: query === MOBILE_QUERY ? mobile : false,
      onchange: null,
      addEventListener: (_: string, listener: () => void) => listeners.add(listener),
      removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
      addListener: (listener: () => void) => listeners.add(listener),
      removeListener: (listener: () => void) => listeners.delete(listener),
      dispatchEvent: () => false,
    }),
  })
}

// Chamar ANTES do render (o renderMobile de test/render.tsx faz isso).
export function setMobileViewport(value: boolean): void {
  mobile = value
  listeners.forEach((listener) => listener())
}

export function resetViewport(): void {
  mobile = false
  listeners.clear()
}
