import { setupServer } from 'msw/node'
import { http, HttpResponse } from 'msw'
import type { ContactRowDto, MetricsDto, RankingEntryDto, SellerResponse } from '../api/types'

export function metrics(overrides: Partial<MetricsDto> = {}): MetricsDto {
  return {
    conversationsStarted: 0,
    conversationsAnswered: 0,
    conversationsUnanswered: 0,
    outboundConversationsStarted: 0,
    outboundConversationsEngaged: 0,
    responseRate: null,
    medianFirstResponseMinutes: null,
    avgResponseMinutes: null,
    minResponseMinutes: null,
    maxResponseMinutes: null,
    responseSamplesCount: 0,
    messagesSent: 0,
    messagesReceived: 0,
    sentReceivedRatio: null,
    readRate: null,
    followUpRate: null,
    sales: 0,
    conversionRate: null,
    avgTimeToCloseBusinessHours: null,
    avgSentPerBusinessHour: null,
    avgReceivedPerBusinessHour: null,
    effectiveBusinessHours: 0,
    lastOutboundMessageAt: null,
    uptimePercent: 100,
    banCount: 0,
    outcomes: [
      { typeCode: 'sale', name: 'Vendas', count: 0, rate: null, avgTimeToCloseBusinessHours: null },
      { typeCode: 'lost', name: 'Clientes perdidos', count: 0, rate: null, avgTimeToCloseBusinessHours: null },
    ],
    ...overrides,
  }
}

export function rankingEntry(name: string, overrides: Partial<MetricsDto> = {}): RankingEntryDto {
  return { sellerId: crypto.randomUUID(), name, metrics: metrics(overrides) }
}

export function contactRow(name: string, overrides: Partial<ContactRowDto> = {}): ContactRowDto {
  return {
    contactId: crypto.randomUUID(),
    name,
    phone: '5511700001111',
    firstMessageAt: '2026-07-01T13:00:00Z',
    lastMessageAt: '2026-07-10T13:00:00Z',
    outcomeTypeCode: null,
    outcome: null,
    labels: [],
    sellerId: crypto.randomUUID(),
    sellerName: 'Ana',
    sellerNumber: '5511900002222',
    numberStatus: 'Active',
    numberBanned: false,
    ...overrides,
  }
}

export function seller(name: string, active = true): SellerResponse {
  return { id: crypto.randomUUID(), name, active, createdAt: new Date().toISOString() }
}

// Handlers default: tudo vazio. Cada teste sobrescreve com mswServer.use(...).
export const mswServer = setupServer(
  http.get('/api/v1/sellers', () => HttpResponse.json([])),
  http.get('/api/v1/sellers/:id/numbers', () => HttpResponse.json([])),
  http.get('/api/v1/reports/ranking', () => HttpResponse.json([])),
  http.get('/api/v1/holidays', () => HttpResponse.json([])),
  http.get('/api/v1/numbers', () => HttpResponse.json([])),
  http.get('/api/v1/numbers/health', () => HttpResponse.json([])),
  http.get('/api/v1/warmup', () =>
    HttpResponse.json({ enabled: true, warming: 0, mature: 0, atCeiling: 0, curve: [], numbers: [] }),
  ),
  http.get('/api/v1/proxies', () =>
    HttpResponse.json({
      enabled: true,
      activeProxies: 0,
      assignedNumbers: 0,
      numbersWithoutProxy: 0,
      bansInPeriod: 0,
      proxies: [],
      unassigned: [],
    }),
  ),
  http.get('/api/v1/contacts', () =>
    HttpResponse.json({ items: [], page: 1, pageSize: 50, total: 0 }),
  ),
  http.get('/api/v1/outcome-types', () =>
    HttpResponse.json([
      { code: 'sale', name: 'Vendas', sortOrder: 1, active: true, terms: [{ id: 't1', term: 'venda' }] },
      { code: 'lost', name: 'Clientes perdidos', sortOrder: 2, active: true, terms: [] },
    ]),
  ),
  http.get('/api/v1/outcome-labels/suggestions', () => HttpResponse.json([])),
  http.get('/api/v1/reports/export/metrics', () =>
    HttpResponse.json([
      { key: 'conversationsStarted', label: 'Conversas iniciadas' },
      { key: 'sales', label: 'Vendas' },
      { key: 'responseRate', label: 'Taxa de resposta' },
    ]),
  ),
  http.get('/api/v1/ai/analyses', () =>
    HttpResponse.json({ items: [], page: 1, pageSize: 50, total: 0 }),
  ),
  http.get('/api/v1/ai/syntheses', () => HttpResponse.json([])),
  // Nenhuma rodada em andamento e nenhuma anterior: o estado inicial da tela.
  http.get('/api/v1/ai/status', () =>
    HttpResponse.json({ running: null, lastAnalysis: null, lastSynthesis: null }),
  ),
  http.post('/api/v1/ai/estimate', () =>
    HttpResponse.json({
      conversations: 12,
      cached: 4,
      sellers: 2,
      estimatedBrl: 2.4,
      available: 17.6,
      affordable: true,
      budgetEnabled: true,
      truncated: false,
    }),
  ),
  http.get('/api/v1/ai/loss-reasons', () =>
    HttpResponse.json([
      { code: 'preco', label: 'Preço' },
      { code: 'sumiu', label: 'Cliente sumiu' },
    ]),
  ),
)

export { http, HttpResponse }
