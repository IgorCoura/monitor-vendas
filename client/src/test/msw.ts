import { setupServer } from 'msw/node'
import { http, HttpResponse } from 'msw'
import type { MetricsDto, RankingEntryDto, SellerResponse } from '../api/types'

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

export function seller(name: string, active = true): SellerResponse {
  return { id: crypto.randomUUID(), name, active, createdAt: new Date().toISOString() }
}

// Handlers default: tudo vazio. Cada teste sobrescreve com mswServer.use(...).
export const mswServer = setupServer(
  http.get('/api/v1/sellers', () => HttpResponse.json([])),
  http.get('/api/v1/sellers/:id/numbers', () => HttpResponse.json([])),
  http.get('/api/v1/reports/ranking', () => HttpResponse.json([])),
  http.get('/api/v1/holidays', () => HttpResponse.json([])),
  http.get('/api/v1/outcome-types', () =>
    HttpResponse.json([
      { code: 'sale', name: 'Vendas', sortOrder: 1, active: true, terms: [{ id: 't1', term: 'venda' }] },
      { code: 'lost', name: 'Clientes perdidos', sortOrder: 2, active: true, terms: [] },
    ]),
  ),
  http.get('/api/v1/outcome-labels/suggestions', () => HttpResponse.json([])),
)

export { http, HttpResponse }
