/// <reference types="vite/client" />

interface ImportMetaEnv {
  // Base das chamadas à API, gravada no bundle **em tempo de build**. O default
  // relativo mantém tudo na mesma origem (o nginx encaminha /api), que é o
  // arranjo sem CORS. Só vale mudar para uma URL absoluta se o navegador tiver
  // de falar com outro domínio — e aí o CORS da API precisa liberar a origem.
  readonly VITE_API_BASE_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
