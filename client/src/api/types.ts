// Espelho dos DTOs da API (server/src/MonitorVendas.Api).

export type NumberStatus = 'Disconnected' | 'Active' | 'BannedTemporary' | 'BannedPermanent'

export interface SellerResponse {
  id: string
  name: string
  active: boolean
  createdAt: string
}

export interface QrCodeDto {
  code?: string | null
  base64?: string | null
  pairingCode?: string | null
}

export interface NumberResponse {
  id: string
  sellerId: string
  phone: string
  instanceName: string
  status: NumberStatus
  createdAt: string
}

export interface CreateNumberResponse {
  number: NumberResponse
  qr: QrCodeDto | null
}

export interface MetricsDto {
  conversationsStarted: number
  conversationsAnswered: number
  conversationsUnanswered: number
  outboundConversationsStarted: number
  outboundConversationsEngaged: number
  responseRate: number | null
  medianFirstResponseMinutes: number | null
  avgResponseMinutes: number | null
  minResponseMinutes: number | null
  maxResponseMinutes: number | null
  responseSamplesCount: number
  messagesSent: number
  messagesReceived: number
  sentReceivedRatio: number | null
  readRate: number | null
  followUpRate: number | null
  sales: number
  conversionRate: number | null
  avgTimeToCloseBusinessHours: number | null
  avgSentPerBusinessHour: number | null
  avgReceivedPerBusinessHour: number | null
  effectiveBusinessHours: number
  lastOutboundMessageAt: string | null
  uptimePercent: number
  banCount: number
  outcomes: OutcomeMetricDto[]
}

// Um desfecho por tipo (venda, cliente perdido, e os que o usuário criar).
export interface OutcomeMetricDto {
  typeCode: string
  name: string
  count: number
  rate: number | null
  avgTimeToCloseBusinessHours: number | null
}

export interface OutcomeTermDto {
  id: string
  term: string
}

export interface OutcomeTypeDto {
  code: string
  name: string
  sortOrder: number
  active: boolean
  terms: OutcomeTermDto[]
}

export interface LabelSuggestionDto {
  labelId: string
  name: string
  conversations: number
  mappedToTypeCode: string | null
}

export interface NumberReportDto {
  numberId: string
  phone: string
  status: NumberStatus
  metrics: MetricsDto
}

export interface SellerReportDto {
  sellerId: string
  name: string
  from: string
  to: string
  totals: MetricsDto
  numbers: NumberReportDto[]
}

export interface RankingEntryDto {
  sellerId: string
  name: string
  metrics: MetricsDto
}

export interface HolidayResponse {
  id: string
  date: string
  name: string
}
