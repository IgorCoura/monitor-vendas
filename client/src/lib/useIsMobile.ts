import { useSyncExternalStore } from 'react'

// 767px é a borda de baixo do `md` do Tailwind (48rem): o que o CSS considera
// mobile e o que o JS considera mobile são sempre a mesma coisa. Mudar aqui sem
// mudar os breakpoints das classes faz as duas visões discordarem no meio.
export const MOBILE_QUERY = '(max-width: 767px)'

function subscribe(onChange: () => void): () => void {
  const query = window.matchMedia(MOBILE_QUERY)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}

function getSnapshot(): boolean {
  return window.matchMedia(MOBILE_QUERY).matches
}

// Sem janela (SSR/teste sem DOM) a resposta é "desktop": é o layout que já
// existia, então o pior caso é continuar como antes.
function getServerSnapshot(): boolean {
  return false
}

export function useIsMobile(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot)
}
