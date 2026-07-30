import type { MutableRefObject } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type DateRange } from './client'

export function useSellers() {
  return useQuery({ queryKey: ['sellers'], queryFn: api.sellers.list })
}

export function useNumbers(sellerId: string) {
  return useQuery({
    queryKey: ['numbers', sellerId],
    queryFn: () => api.numbers.list(sellerId),
    enabled: !!sellerId,
  })
}

// pollMs = null desliga a atualização automática (o usuário escolhe na barra de
// atualização); a busca manual continua disponível via refetch().
//
// `freshRef` é levantado pelo botão de atualizar: a próxima busca vai com
// Cache-Control: no-cache para o servidor recalcular em vez de servir o cache.
function takeFresh(freshRef?: MutableRefObject<boolean>): boolean {
  if (!freshRef?.current) return false
  freshRef.current = false
  return true
}

export function useRanking(
  range: DateRange,
  pollMs: number | null = 60_000,
  freshRef?: MutableRefObject<boolean>,
) {
  return useQuery({
    queryKey: ['ranking', range.from, range.to],
    queryFn: () => api.reports.ranking(range, takeFresh(freshRef)),
    refetchInterval: pollMs ?? false,
  })
}

export function useSellerReport(
  id: string,
  range: DateRange,
  pollMs: number | null = 60_000,
  freshRef?: MutableRefObject<boolean>,
) {
  return useQuery({
    queryKey: ['seller-report', id, range.from, range.to],
    queryFn: () => api.reports.seller(id, range, takeFresh(freshRef)),
    enabled: !!id,
    refetchInterval: pollMs ?? false,
  })
}

export function useHolidays() {
  return useQuery({ queryKey: ['holidays'], queryFn: api.holidays.list })
}

export function useOutcomeTypes() {
  return useQuery({ queryKey: ['outcome-types'], queryFn: api.outcomeTypes.list })
}

export function useLabelSuggestions() {
  return useQuery({ queryKey: ['label-suggestions'], queryFn: api.outcomeTypes.suggestions })
}

// Mudar o catálogo recalcula os desfechos no servidor: os relatórios precisam ser
// invalidados junto.
function useCatalogMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ['outcome-types'] })
      client.invalidateQueries({ queryKey: ['label-suggestions'] })
      client.invalidateQueries({ queryKey: ['ranking'] })
      client.invalidateQueries({ queryKey: ['seller-report'] })
    },
  })
}

export function useCreateOutcomeType() {
  return useCatalogMutation(({ code, name }: { code: string; name: string }) =>
    api.outcomeTypes.create(code, name),
  )
}

export function useDeleteOutcomeType() {
  return useCatalogMutation((code: string) => api.outcomeTypes.remove(code))
}

export function useAddOutcomeTerm() {
  return useCatalogMutation(({ code, term }: { code: string; term: string }) =>
    api.outcomeTypes.addTerm(code, term),
  )
}

export function useRemoveOutcomeTerm() {
  return useCatalogMutation(({ code, termId }: { code: string; termId: string }) =>
    api.outcomeTypes.removeTerm(code, termId),
  )
}

export function useCreateSeller() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: api.sellers.create,
    onSuccess: () => client.invalidateQueries({ queryKey: ['sellers'] }),
  })
}

export function useUpdateSeller() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name, active }: { id: string; name: string; active: boolean }) =>
      api.sellers.update(id, { name, active }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['sellers'] }),
  })
}

export function useCreateNumber() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ sellerId, phone }: { sellerId: string; phone: string }) =>
      api.numbers.create(sellerId, phone),
    onSuccess: (_, { sellerId }) =>
      client.invalidateQueries({ queryKey: ['numbers', sellerId] }),
  })
}

export function useConnectNumber() {
  return useMutation({ mutationFn: api.numbers.connect })
}

export function useBanPermanent() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: api.numbers.banPermanent,
    onSuccess: () => client.invalidateQueries({ queryKey: ['numbers'] }),
  })
}

export function useCreateHoliday() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ date, name }: { date: string; name: string }) => api.holidays.create(date, name),
    onSuccess: () => client.invalidateQueries({ queryKey: ['holidays'] }),
  })
}

export function useDeleteHoliday() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: api.holidays.remove,
    onSuccess: () => client.invalidateQueries({ queryKey: ['holidays'] }),
  })
}
