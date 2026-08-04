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
  // Cooldown pós-ban: reconectar antes disso exige confirmação explícita.
  bannedUntil: string | null
  // Pausa de envio após restrição do WhatsApp (erro 463).
  sendingPausedUntil: string | null
  sendingPauseReason: string | null
}

// Proxies contratados no fornecedor. A senha nunca vem na resposta.
export type ProxyStatus = 'Active' | 'Paused' | 'Suspect' | 'Expired' | 'Revoked' | 'Failed'

export interface ProxyNumberDto {
  numberId: string
  phone: string
  sellerName: string
  status: NumberStatus
}

export interface ProxyDto {
  id: string
  shortId: string
  label: string
  kind: string
  host: string
  port: number
  status: ProxyStatus
  numbersCount: number
  capacity: number
  sellersCount: number
  bansCount: number
  bannedNumbersCount: number
  expiresAt: string | null
  lastTestedAt: string | null
  lastTestOk: boolean | null
  numbers: ProxyNumberDto[]
}

export interface ProxyOverviewDto {
  enabled: boolean
  activeProxies: number
  assignedNumbers: number
  numbersWithoutProxy: number
  bansInPeriod: number
  proxies: ProxyDto[]
  unassigned: ProxyNumberDto[]
}

export interface ProxyMoveDto {
  numberId: string
  phone: string
  sellerName: string
  fromLabel: string | null
  toLabel: string
  restartsSocket: boolean
}

export interface AllocationPreviewDto {
  moves: ProxyMoveDto[]
  stillWithoutProxy: string[]
}

// Aviso de risco anti-ban: a ação pode prosseguir, mas quem opera vê isto antes.
export interface RiskWarning {
  code: string
  message: string
}

// Semáforo de saúde do número: sinais que preveem ban antes de ele acontecer.
// NoData = número sem tráfego (não é alarme).
export type HealthLevel = 'NoData' | 'Low' | 'Medium' | 'High' | 'Critical'

export interface HealthSignalDto {
  key: string
  value: string
  points: number
}

export interface NumberHealthDto {
  numberId: string
  phone: string
  sellerId: string
  sellerName: string
  status: NumberStatus
  score: number
  level: HealthLevel
  signals: HealthSignalDto[]
}

export type PairingStatus = 'AwaitingScan' | 'AwaitingConfirmation' | 'Completed' | 'Rejected'

// Uma tentativa de conectar um WhatsApp. O número não é digitado: vem do próprio
// aparelho que leu o QR, e só então vira cadastro.
export interface PairingSessionDto {
  id: string
  sellerId: string
  status: PairingStatus
  detectedPhone: string | null
  detectedProfileName: string | null
  error: string | null
  requiresTransfer: boolean
  requiresBannedConfirmation: boolean
  currentOwnerName: string | null
  // Conectado AGORA no dono atual: transferir desliga o aparelho de lá.
  currentlyConnected: boolean
  expiresAt: string
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

// Uma linha por contato: vendedor/número/banimento vêm da conversa mais recente
// dentro do período filtrado.
export interface ContactRowDto {
  contactId: string
  name: string
  phone: string
  firstMessageAt: string
  lastMessageAt: string
  outcomeTypeCode: string | null
  outcome: string | null
  labels: string[]
  sellerId: string | null
  sellerName: string | null
  sellerNumber: string | null
  numberStatus: NumberStatus
  numberBanned: boolean
}

export interface ContactPageDto {
  items: ContactRowDto[]
  page: number
  pageSize: number
  total: number
}

export interface NumberWithSellerResponse {
  id: string
  phone: string
  status: NumberStatus
  sellerId: string
  sellerName: string
}

export type ContactShareStatus = 'Pending' | 'Completed' | 'Failed'

export interface ContactShareDto {
  id: string
  senderNumberId: string
  senderPhone: string
  destination: string
  totalContacts: number
  totalMessages: number
  sentMessages: number
  status: ContactShareStatus
  error: string | null
  createdAt: string
  completedAt: string | null
}

export interface ContactFilters {
  from: string
  to: string
  sellerId: string
  outcomeTypes: string[]
  banned: 'all' | 'banned' | 'active'
}

export interface HolidayResponse {
  id: string
  date: string
  name: string
}

export interface AiAnalysisRowDto {
  conversationId: string
  analysisId: string
  sellerId: string | null
  sellerName: string
  sellerNumber: string
  contactName: string
  contactPhone: string
  startedAt: string
  lastMessageAt: string
  realOutcome: string | null
  aiStatus: string | null
  aiStatusCode: string | null
  confidence: number
  divergent: boolean
  evidence: string | null
  lossReason: string | null
  askedForSale: boolean
  ignoredBuyingSignal: boolean
  objections: string | null
  shouldRecontact: boolean
  recontactReason: string | null
  suggestedMessage: string | null
  interest: string | null
  summary: string | null
  conductAlert: string | null
  model: string
  analyzedAt: string
  versions: number
  // Áudios da conversa e quantos o modelo ouviu: `attached < expected` é leitura
  // incompleta, e a tela precisa avisar em vez de parecer completa.
  audioExpected: number
  audioAttached: number
}

export interface AiAnalysisPageDto {
  items: AiAnalysisRowDto[]
  page: number
  pageSize: number
  total: number
}

export interface AiSynthesisDto {
  sellerId: string
  sellerName: string
  overview: string | null
  strengths: string[]
  improvements: string[]
  dominantLossPattern: string | null
  trainingSuggestion: string | null
  conversationsCount: number
  model: string
  createdAt: string
  stale: boolean
}

export type AiJobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed'

export interface AiJobDto {
  id: string
  kind: 'Analyze' | 'Synthesize'
  status: AiJobStatus
  total: number
  processed: number
  skipped: number
  costBrl: number
  error: string | null
  createdAt: string
  completedAt: string | null
}

// O estado que decide se os botões da tela de IA ficam disponíveis. Vem do
// banco, então continua valendo depois de recarregar a página.
export interface AiStatusDto {
  running: AiJobDto | null
  lastAnalysis: AiJobDto | null
  lastSynthesis: AiJobDto | null
}

export interface AiEstimate {
  conversations: number
  cached: number
  sellers: number
  estimatedBrl: number
  available: number
  affordable: boolean
  budgetEnabled: boolean
  truncated: boolean
}

export interface AiLossReason {
  code: string
  label: string
}

export interface AiAnalysisFilters {
  from: string
  to: string
  sellerId: string
  status: string
  lossReason: string
  divergent: '' | 'true' | 'false'
  recontact: '' | 'true' | 'false'
}

export interface ReportMetricOption {
  key: string
  label: string
}

export interface ReportExportFilters {
  from: string
  to: string
  sellerIds: string[]
  metrics: string[]
  charts: string[]
  includeNumbers: boolean
}

export interface WarmupPeerDto {
  peerId: string | null
  numberId: string
  phone: string
  sellerName: string
  numberStatus: string
  inPool: boolean
  ineligibleReason: string | null
  persona: string | null
  coreCircle: number
  occasionalCircle: number
  circle: string[]
  goal: number
  effectiveGoal: number
  cappedByGraph: boolean
  realMessagesToday: number
  warmupMessagesToday: number
}

export interface WarmupTurnDto {
  sequence: number
  fromPhone: string
  text: string
  scheduledAt: string
  sentAt: string | null
  delivered: boolean
}

export interface WarmupConversationDto {
  id: string
  theme: string
  status: string
  phoneA: string
  phoneB: string
  createdAt: string
  completedAt: string | null
  archived: boolean
  turns: WarmupTurnDto[]
}

export interface WarmupOverviewDto {
  enabled: boolean
  haltedAt: string | null
  haltReason: string | null
  peersInPool: number
  messagesToday: number
  conversationsToday: number
  deliveryRate: number | null
  numbers: WarmupPeerDto[]
  conversations: WarmupConversationDto[]
}
