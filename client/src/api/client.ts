import type {
  CreateNumberResponse,
  HolidayResponse,
  LabelSuggestionDto,
  NumberResponse,
  OutcomeTermDto,
  OutcomeTypeDto,
  QrCodeDto,
  RankingEntryDto,
  SellerReportDto,
  SellerResponse,
} from './types'

const BASE = '/api/v1'

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
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
    try {
      const body = await res.json()
      message = body.error ?? body.title ?? message
    } catch {
      // corpo não-JSON: mantém a mensagem genérica
    }
    throw new ApiError(res.status, message)
  }

  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export interface DateRange {
  from: string
  to: string
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
    create: (sellerId: string, phone: string) =>
      request<CreateNumberResponse>(`/sellers/${sellerId}/numbers`, {
        method: 'POST',
        body: JSON.stringify({ phone }),
      }),
    connect: (id: string) => request<QrCodeDto>(`/numbers/${id}/connect`, { method: 'POST' }),
    banPermanent: (id: string) =>
      request<NumberResponse>(`/numbers/${id}/ban-permanent`, { method: 'POST' }),
  },
  reports: {
    seller: (id: string, range: DateRange, fresh = false) =>
      request<SellerReportDto>(`/reports/sellers/${id}?from=${range.from}&to=${range.to}`, { fresh }),
    ranking: (range: DateRange, fresh = false) =>
      request<RankingEntryDto[]>(`/reports/ranking?from=${range.from}&to=${range.to}`, { fresh }),
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
  holidays: {
    list: () => request<HolidayResponse[]>('/holidays'),
    create: (date: string, name: string) =>
      request<HolidayResponse>('/holidays', { method: 'POST', body: JSON.stringify({ date, name }) }),
    remove: (id: string) => request<void>(`/holidays/${id}`, { method: 'DELETE' }),
  },
}
