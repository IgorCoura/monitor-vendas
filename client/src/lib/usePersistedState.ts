import { useEffect, useRef, useState } from 'react'

// Preferência de UI persistida em localStorage. Para listas de visibilidade
// guardamos os itens OCULTOS: métricas novas passam a aparecer por default sem
// depender do que o usuário salvou antes.
//
// `sanitize` valida o que veio do storage (chave de métrica que não existe mais,
// JSON de outra versão do app): sem isso um valor velho quebraria a tela.
export function usePersistedState<T>(
  key: string,
  initial: T,
  sanitize?: (value: unknown) => T,
) {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = localStorage.getItem(key)
      if (raw === null) return initial
      const parsed: unknown = JSON.parse(raw)
      return sanitize ? sanitize(parsed) : (parsed as T)
    } catch {
      return initial
    }
  })

  // Só grava em mudança real: escrever na montagem sobrescreveria a preferência
  // salva pelo valor default sempre que a leitura falhasse (JSON inválido, erro
  // no sanitize), apagando em silêncio a escolha do usuário.
  const mounted = useRef(false)
  useEffect(() => {
    if (!mounted.current) {
      mounted.current = true
      return
    }
    try {
      localStorage.setItem(key, JSON.stringify(value))
    } catch {
      // storage indisponível (modo privado etc.): a preferência só não persiste
    }
  }, [key, value])

  return [value, setValue] as const
}
