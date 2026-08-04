import type { ReactNode } from 'react'

// FONTE ÚNICA das rotas de navegação. Antes a sidebar e a barra inferior tinham
// arrays independentes, e a regra "rota nova entra nos dois lugares" existia
// porque era fácil esquecer um deles. Com uma lista só, esquecer deixa de ser
// possível: quem adiciona aqui aparece nos dois.

function DashboardIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <rect x="2.5" y="9" width="3.5" height="8.5" rx="1" />
      <rect x="8.25" y="4.5" width="3.5" height="13" rx="1" />
      <rect x="14" y="11.5" width="3.5" height="6" rx="1" />
    </svg>
  )
}

function RegistryIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M7 3.5a3 3 0 1 1 0 6 3 3 0 0 1 0-6Zm0 7.25c3.04 0 5.5 1.6 5.5 3.58v1.17a1 1 0 0 1-1 1H2.5a1 1 0 0 1-1-1v-1.17c0-1.98 2.46-3.58 5.5-3.58Z" />
      <path d="M14 6.5h4a.75.75 0 0 1 0 1.5h-4a.75.75 0 0 1 0-1.5Zm0 3.25h4a.75.75 0 0 1 0 1.5h-4a.75.75 0 0 1 0-1.5Z" />
    </svg>
  )
}

function ContactsIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M4 2.5h12a1.5 1.5 0 0 1 1.5 1.5v12a1.5 1.5 0 0 1-1.5 1.5H4A1.5 1.5 0 0 1 2.5 16V4A1.5 1.5 0 0 1 4 2.5Zm6 3a2.25 2.25 0 1 0 0 4.5 2.25 2.25 0 0 0 0-4.5Zm0 5.5c-2.2 0-4 1.16-4 2.6v.65h8v-.65c0-1.44-1.8-2.6-4-2.6Z" />
    </svg>
  )
}

function AiIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M10 1.5l1.35 3.9 3.9 1.35-3.9 1.35L10 12l-1.35-3.9-3.9-1.35 3.9-1.35L10 1.5Zm5.25 9.5l.72 2.08 2.08.72-2.08.72-.72 2.08-.72-2.08-2.08-.72 2.08-.72.72-2.08Zm-10 1.5l.54 1.56 1.56.54-1.56.54-.54 1.56-.54-1.56-1.56-.54 1.56-.54.54-1.56Z" />
    </svg>
  )
}

function LabelsIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M9.6 2.5H16a1.5 1.5 0 0 1 1.5 1.5v6.4a1.5 1.5 0 0 1-.44 1.06l-5.6 5.6a1.5 1.5 0 0 1-2.12 0l-6.4-6.4a1.5 1.5 0 0 1 0-2.12l5.6-5.6A1.5 1.5 0 0 1 9.6 2.5Zm3.65 2.75a1.25 1.25 0 1 0 0 2.5 1.25 1.25 0 0 0 0-2.5Z" />
    </svg>
  )
}

function HolidaysIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M6 1.75c.41 0 .75.34.75.75v1h6.5v-1a.75.75 0 0 1 1.5 0v1H16A1.5 1.5 0 0 1 17.5 5v11a1.5 1.5 0 0 1-1.5 1.5H4A1.5 1.5 0 0 1 2.5 16V5A1.5 1.5 0 0 1 4 3.5h1.25v-1c0-.41.34-.75.75-.75ZM4 7.5V16h12V7.5H4Z" />
    </svg>
  )
}

function ProxiesIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M10 1.75a8.25 8.25 0 1 0 0 16.5 8.25 8.25 0 0 0 0-16.5ZM3.3 10c0-.7.11-1.36.31-1.99h2.2a15.6 15.6 0 0 0 0 3.98h-2.2A6.7 6.7 0 0 1 3.3 10Zm4.02 0c0-.69.05-1.35.13-1.99h5.1a15.6 15.6 0 0 1 0 3.98h-5.1A15.6 15.6 0 0 1 7.32 10Zm6.87-1.99h2.2a6.73 6.73 0 0 1 0 3.98h-2.2a15.6 15.6 0 0 0 0-3.98ZM10 3.3c.74 0 1.7 1.13 2.2 3.21H7.8C8.3 4.43 9.26 3.3 10 3.3Zm0 13.4c-.74 0-1.7-1.13-2.2-3.21h4.4c-.5 2.08-1.46 3.21-2.2 3.21Z" />
    </svg>
  )
}

function WarmupIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-5 w-5">
      <path d="M10 1.5c.3 2.2-.6 3.4-1.7 4.5C7 7.3 5.5 8.6 5.5 11.2a4.5 4.5 0 0 0 9 0c0-1.6-.6-2.7-1.4-3.7-.3 .6-.8 1-1.4 1.2.2-2.4-.8-5.3-1.7-7.2Zm0 14.2a2.4 2.4 0 0 1-2.4-2.5c0-1.3.8-2 1.4-2.7.4-.5.8-1 .9-1.7.6.8 1.4 1.6 1.9 2.4.4.6.6 1.2.6 2a2.4 2.4 0 0 1-2.4 2.5Z" />
    </svg>
  )
}

export interface NavRoute {
  to: string
  // Rótulo da sidebar (desktop), onde há espaço.
  label: string
  // Rótulo curto da barra inferior e do menu do celular.
  mobileLabel: string
  icon: ReactNode
  // Rota de uso diário: fica visível na barra inferior. As demais entram no "⋯".
  primary: boolean
}

export const navRoutes: NavRoute[] = [
  { to: '/', label: 'Dashboard', mobileLabel: 'Painel', icon: <DashboardIcon />, primary: true },
  { to: '/registry', label: 'Cadastros', mobileLabel: 'Cadastros', icon: <RegistryIcon />, primary: true },
  { to: '/contacts', label: 'Contatos', mobileLabel: 'Contatos', icon: <ContactsIcon />, primary: true },
  { to: '/ai', label: 'Análises IA', mobileLabel: 'IA', icon: <AiIcon />, primary: true },
  { to: '/warmup', label: 'Aquecimento', mobileLabel: 'Aquecimento', icon: <WarmupIcon />, primary: false },
  { to: '/proxies', label: 'Proxies', mobileLabel: 'Proxies', icon: <ProxiesIcon />, primary: false },
  { to: '/labels', label: 'Etiquetas', mobileLabel: 'Etiquetas', icon: <LabelsIcon />, primary: false },
  { to: '/holidays', label: 'Feriados', mobileLabel: 'Feriados', icon: <HolidaysIcon />, primary: false },
]

// Quatro abas na barra + o botão "Mais" dão ~78px cada em 390px, contra os ~56px
// de quando todas as rotas cabiam lá — os rótulos voltam a caber inteiros.
export const primaryRoutes = navRoutes.filter((r) => r.primary)
export const overflowRoutes = navRoutes.filter((r) => !r.primary)
