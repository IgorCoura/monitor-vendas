import type {
  AiAnalysisFilters,
  AiAnalysisPageDto,
  AiEstimate,
  AiJobDto,
  AiLossReason,
  AiStatusDto,
  AiSynthesisDto,
  ContactFilters,
  ContactPageDto,
  ContactShareDto,
  NumberHealthDto,
  NumberWithSellerResponse,
  PairingSessionDto,
  HolidayResponse,
  LabelSuggestionDto,
  NumberResponse,
  OutcomeTermDto,
  OutcomeTypeDto,
  QrCodeDto,
  RankingEntryDto,
  ReportExportFilters,
  AllocationPreviewDto,
  ProxyOverviewDto,
  ReportMetricOption,
  RiskWarning,
  SellerReportDto,
  SellerResponse,
  WarmupOverviewDto,
} from './types'

// Relativo por default: o navegador chama a própria origem e o nginx encaminha
// /api para a API, sem CORS. Uma URL absoluta aqui é gravada no bundle e vale
// para aquele build — para trocar o destino sem rebuild, use API_URL no
// container do front, que é lido pelo nginx a cada start.
//
// Regressão (31/07/2026): com `??`, a string vazia passava. O Dockerfile faz
// `ENV VITE_API_BASE_URL=$VITE_API_BASE_URL` e, sem build-arg, isso define a
// variável como "" — o bundle saía chamando `/reports/ranking` em vez de
// `/api/v1/reports/ranking`, o nginx devolvia o index.html e a tela recebia
// HTML onde esperava JSON, com status 200. Vazio aqui é ausência.
export function resolveApiBase(configured?: string): string {
  return configured?.trim() ? configured : '/api/v1'
}

const BASE = resolveApiBase(import.meta.env.VITE_API_BASE_URL)

export class ApiError extends Error {
  readonly status: number

  // Alguns 409 não são erro, são pergunta: o servidor descreve o risco e espera
  // um "sim". A tela precisa dos avisos para mostrar ANTES de reperguntar.
  readonly requiresConfirmation: boolean
  readonly warnings: RiskWarning[]

  constructor(status: number, message: string, requiresConfirmation = false, warnings: RiskWarning[] = []) {
    super(message)
    this.status = status
    this.requiresConfirmation = requiresConfirmation
    this.warnings = warnings
  }
}

async function request<T>(path: string, init?: RequestInit & { fresh?: boolean }): Promise<T> {
  const { fresh, ...rest } = init ?? {}
  const res = await fetch(`${BASE}${path}`, {
    ...rest,
    headers: {
      'Content-Type': 'application/json',
      // Atualização manual ignora o cache do servidor.
      ...(fresh ? { 'Cache-Control': 'no-cache' } : {}),
      ...rest.headers,
    },
  })

  if (!res.ok) {
    let message = `Erro ${res.status}`
    let requiresConfirmation = false
    let warnings: RiskWarning[] = []
    try {
      const body = await res.json()
      message = body.error ?? body.title ?? message
      requiresConfirmation = body.requiresConfirmation === true
      warnings = Array.isArray(body.warnings) ? body.warnings : []
    } catch {
      // corpo não-JSON: mantém a mensagem genérica
    }
    throw new ApiError(res.status, message, requiresConfirmation, warnings)
  }

  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export interface DateRange {
  from: string
  to: string
}

// Os mesmos filtros alimentam a prévia e o arquivo — a planilha é sempre o que
// está na tela.
// Flags de confirmação da reconexão (ban permanente e cooldown pós-ban).
function confirmFlags(confirmBanned: boolean, confirmCooldown: boolean): string {
  const flags = [
    ...(confirmBanned ? ['confirmBanned=true'] : []),
    ...(confirmCooldown ? ['confirmCooldown=true'] : []),
  ]
  return flags.length > 0 ? `?${flags.join('&')}` : ''
}

function contactQuery(filters: ContactFilters): URLSearchParams {
  const params = new URLSearchParams({ from: filters.from, to: filters.to })
  if (filters.sellerId) params.set('sellerId', filters.sellerId)
  if (filters.outcomeTypes.length > 0) params.set('outcomeTypes', filters.outcomeTypes.join(','))
  if (filters.banned !== 'all') params.set('banned', String(filters.banned === 'banned'))
  return params
}

function analysisQuery(filters: AiAnalysisFilters): URLSearchParams {
  const params = new URLSearchParams({ from: filters.from, to: filters.to })
  if (filters.sellerId) params.set('sellerId', filters.sellerId)
  if (filters.status) params.set('status', filters.status)
  if (filters.lossReason) params.set('lossReason', filters.lossReason)
  if (filters.divergent) params.set('divergent', filters.divergent)
  if (filters.recontact) params.set('recontact', filters.recontact)
  return params
}

// Listas vão como CSV, no mesmo formato dos filtros de contatos.
function exportQuery(filters: ReportExportFilters): URLSearchParams {
  const params = new URLSearchParams({ from: filters.from, to: filters.to })
  if (filters.sellerIds.length > 0) params.set('sellerIds', filters.sellerIds.join(','))
  if (filters.metrics.length > 0) params.set('metrics', filters.metrics.join(','))
  if (filters.charts.length > 0) params.set('charts', filters.charts.join(','))
  if (!filters.includeNumbers) params.set('includeNumbers', 'false')
  return params
}

function runBody(filters: AiAnalysisFilters, conversationIds: string[], includeAudio = false) {
  return {
    from: filters.from,
    to: filters.to,
    sellerIds: filters.sellerId ? [filters.sellerId] : [],
    conversationIds,
    includeAudio,
  }
}

export const api = {
  sellers: {
    list: () => request<SellerResponse[]>('/sellers'),
    create: (name: string) =>
      request<SellerResponse>('/sellers', { method: 'POST', body: JSON.stringify({ name }) }),
    update: (id: string, body: { name: string; active: boolean }) =>
      request<SellerResponse>(`/sellers/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  },
  numbers: {
    list: (sellerId: string) => request<NumberResponse[]>(`/sellers/${sellerId}/numbers`),
    listAll: () => request<NumberWithSellerResponse[]>('/numbers'),
    // Semáforo de saúde (últimos 7 dias por default do servidor).
    health: () => request<NumberHealthDto[]>('/numbers/health'),
    // Reconectar um número que já existe. Ban permanente é decisão manual e o
    // cooldown pós-ban segura a pressa: os dois só passam com confirmação.
    connect: (id: string, confirmBanned = false, confirmCooldown = false) =>
      request<QrCodeDto>(`/numbers/${id}/connect${confirmFlags(confirmBanned, confirmCooldown)}`, {
        method: 'POST',
      }),
    // Só sob pedido: recria a instância com o número para o código nascer válido.
    pairingCode: (id: string, confirmBanned = false, confirmCooldown = false) =>
      request<QrCodeDto>(`/numbers/${id}/pairing-code${confirmFlags(confirmBanned, confirmCooldown)}`, {
        method: 'POST',
      }),
    // Desvincula o aparelho: só volta com QR ou código novo.
    disconnect: (id: string) =>
      request<NumberResponse>(`/numbers/${id}/disconnect`, { method: 'POST' }),
    // Chacoalha o socket sem desvincular — para instância travada.
    restart: (id: string, confirmCooldown = false) =>
      request<NumberResponse>(`/numbers/${id}/restart${confirmCooldown ? '?confirmCooldown=true' : ''}`, {
        method: 'POST',
      }),
    transfer: (id: string, sellerId: string) =>
      request<NumberResponse>(`/numbers/${id}/transfer`, {
        method: 'POST',
        body: JSON.stringify({ sellerId }),
      }),
    banPermanent: (id: string) =>
      request<NumberResponse>(`/numbers/${id}/ban-permanent`, { method: 'POST' }),
  },
  // O número nasce do WhatsApp que leu o QR, nunca de um campo digitado.
  pairings: {
    start: (sellerId: string) =>
      request<PairingSessionDto>(`/sellers/${sellerId}/pairings`, { method: 'POST' }),
    status: (id: string) => request<PairingSessionDto>(`/pairings/${id}`),
    confirm: (id: string) => request<PairingSessionDto>(`/pairings/${id}/confirm`, { method: 'POST' }),
    cancel: (id: string) => request<PairingSessionDto>(`/pairings/${id}/cancel`, { method: 'POST' }),
    // O telefone só diz ao WhatsApp para quem mandar o código; o cadastro
    // continua vindo do aparelho que conectar.
    pairingCode: (id: string, phone: string) =>
      request<PairingSessionDto>(`/pairings/${id}/pairing-code`, {
        method: 'POST',
        body: JSON.stringify({ phone }),
      }),
  },
  reports: {
    seller: (id: string, range: DateRange, fresh = false) =>
      request<SellerReportDto>(`/reports/sellers/${id}?from=${range.from}&to=${range.to}`, { fresh }),
    ranking: (range: DateRange, fresh = false) =>
      request<RankingEntryDto[]>(`/reports/ranking?from=${range.from}&to=${range.to}`, { fresh }),
    exportMetrics: () => request<ReportMetricOption[]>('/reports/export/metrics'),
    // Igual à exportação de contatos: o navegador baixa e o nome do arquivo vem
    // do Content-Disposition.
    exportUrl: (filters: ReportExportFilters) => `${BASE}/reports/export?${exportQuery(filters)}`,
  },
  ai: {
    lossReasons: () => request<AiLossReason[]>('/ai/loss-reasons'),
    analyses: (filters: AiAnalysisFilters, page: number, pageSize: number) => {
      const params = analysisQuery(filters)
      params.set('page', String(page))
      params.set('pageSize', String(pageSize))
      return request<AiAnalysisPageDto>(`/ai/analyses?${params}`)
    },
    syntheses: (sellerId: string) =>
      request<AiSynthesisDto[]>(`/ai/syntheses${sellerId ? `?sellerId=${sellerId}` : ''}`),
    // O download é do navegador, como o de contatos: exporta as leituras que já
    // existem, sem chamar a IA.
    analysesExportUrl: (filters: AiAnalysisFilters) =>
      `${BASE}/ai/analyses/export?${analysisQuery(filters)}`,
    status: () => request<AiStatusDto>('/ai/status'),
    // Quanto a rodada vai custar, antes de confirmar.
    estimate: (kind: 'Analyze' | 'Synthesize', filters: AiAnalysisFilters, includeAudio = false) =>
      request<AiEstimate>('/ai/estimate', {
        method: 'POST',
        body: JSON.stringify({ kind, ...runBody(filters, [], includeAudio) }),
      }),
    // Sem `force`: o servidor refaz só o que mudou. Reprocessar tudo existe na
    // API, mas a tela não pede — pagar de novo pela mesma leitura não compensa.
    runAnalyses: (filters: AiAnalysisFilters, conversationIds: string[], includeAudio = false) =>
      request<AiJobDto>('/ai/analyses/run', {
        method: 'POST',
        body: JSON.stringify(runBody(filters, conversationIds, includeAudio)),
      }),
    runSyntheses: (filters: AiAnalysisFilters) =>
      request<AiJobDto>('/ai/syntheses/run', { method: 'POST', body: JSON.stringify(runBody(filters, [])) }),
  },
  outcomeTypes: {
    list: () => request<OutcomeTypeDto[]>('/outcome-types'),
    suggestions: () => request<LabelSuggestionDto[]>('/outcome-labels/suggestions'),
    create: (code: string, name: string) =>
      request<OutcomeTypeDto>('/outcome-types', { method: 'POST', body: JSON.stringify({ code, name }) }),
    remove: (code: string) => request<void>(`/outcome-types/${code}`, { method: 'DELETE' }),
    addTerm: (code: string, term: string) =>
      request<OutcomeTermDto>(`/outcome-types/${code}/terms`, { method: 'POST', body: JSON.stringify({ term }) }),
    removeTerm: (code: string, termId: string) =>
      request<void>(`/outcome-types/${code}/terms/${termId}`, { method: 'DELETE' }),
  },
  contacts: {
    list: (filters: ContactFilters, page: number, pageSize: number) => {
      const params = contactQuery(filters)
      params.set('page', String(page))
      params.set('pageSize', String(pageSize))
      return request<ContactPageDto>(`/contacts?${params}`)
    },
    // O download é feito pelo navegador: assim o nome do arquivo vem do
    // Content-Disposition do servidor.
    exportUrl: (filters: ContactFilters) => `${BASE}/contacts/export?${contactQuery(filters)}`,
    // `confirmRisk` = o operador viu os avisos anti-ban e mandou enviar assim mesmo.
    share: (
      filters: ContactFilters,
      senderNumberId: string,
      destination: string,
      confirmRisk = false,
    ) =>
      request<ContactShareDto>(
        `/contacts/share?${contactQuery(filters)}${confirmRisk ? '&confirmRisk=true' : ''}`,
        {
          method: 'POST',
          body: JSON.stringify({ senderNumberId, destination }),
        },
      ),
    shareStatus: (id: string) => request<ContactShareDto>(`/contacts/share/${id}`),
  },
  proxies: {
    overview: () => request<ProxyOverviewDto>('/proxies'),
    settings: (enabled: boolean) =>
      request<{ enabled: boolean }>('/proxies/settings', {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
    sync: () => request<{ synced: number }>('/proxies/sync', { method: 'POST' }),
    test: (id: string) => request<{ tested: boolean | null }>(`/proxies/${id}/test`, { method: 'POST' }),
    pause: (id: string) => request<{ status: string }>(`/proxies/${id}/pause`, { method: 'POST' }),
    resume: (id: string) => request<{ status: string }>(`/proxies/${id}/resume`, { method: 'POST' }),
    // Prévia antes de aplicar: cada número conectado que muda de proxy custa um
    // restart de socket, então o operador vê o plano primeiro.
    preview: (rebalance: boolean) =>
      request<AllocationPreviewDto>(`/proxies/allocation/preview?rebalance=${rebalance}`),
    apply: (rebalance: boolean) =>
      request<{ moved: number; withoutProxy: number }>(
        `/proxies/allocation/apply?rebalance=${rebalance}`,
        { method: 'POST' },
      ),
    detach: (numberId: string) => request<void>(`/numbers/${numberId}/proxy`, { method: 'DELETE' }),
  },
  warmup: {
    overview: () => request<WarmupOverviewDto>('/warmup'),
    settings: (enabled: boolean) =>
      request<{ enabled: boolean }>('/warmup/settings', {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
    // Botão de pânico: para tudo agora, sem desligar o interruptor.
    halt: () => request<void>('/warmup/halt', { method: 'POST' }),
    addPeer: (numberId: string) =>
      request<void>('/warmup/peers', { method: 'POST', body: JSON.stringify({ numberId }) }),
    removePeer: (numberId: string) => request<void>(`/warmup/peers/${numberId}`, { method: 'DELETE' }),
  },
  holidays: {
    list: () => request<HolidayResponse[]>('/holidays'),
    create: (date: string, name: string) =>
      request<HolidayResponse>('/holidays', { method: 'POST', body: JSON.stringify({ date, name }) }),
    remove: (id: string) => request<void>(`/holidays/${id}`, { method: 'DELETE' }),
  },
}
