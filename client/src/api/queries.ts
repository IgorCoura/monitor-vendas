import { useRef, type MutableRefObject } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type DateRange } from './client'
import type { AiAnalysisFilters, ContactFilters } from './types'

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

export function useContacts(filters: ContactFilters, page: number, pageSize = 50) {
  return useQuery({
    queryKey: ['contacts', filters, page, pageSize],
    queryFn: () => api.contacts.list(filters, page, pageSize),
    placeholderData: (previous) => previous,
  })
}

export function useAllNumbers() {
  return useQuery({ queryKey: ['numbers', 'all'], queryFn: api.numbers.listAll })
}

// Semáforo de saúde dos números. Busca em segundo plano: sem círculo de
// progresso — o sinal de atualização é o do UpdateControls, como nas métricas.
export function useNumbersHealth() {
  return useQuery({ queryKey: ['numbers', 'health'], queryFn: api.numbers.health })
}

export function useProxies() {
  return useQuery({ queryKey: ['proxies'], queryFn: api.proxies.overview })
}

// Toda ação de proxy mexe na lista e pode mexer nos números: as duas são
// invalidadas juntas.
function useProxyMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ['proxies'] })
      client.invalidateQueries({ queryKey: ['numbers'] })
    },
  })
}

export function useToggleProxies() {
  return useProxyMutation((enabled: boolean) => api.proxies.settings(enabled))
}

export function useSyncProxies() {
  return useProxyMutation<void>(() => api.proxies.sync())
}

export function useTestProxy() {
  return useProxyMutation((id: string) => api.proxies.test(id))
}

export function usePauseProxy() {
  return useProxyMutation(({ id, paused }: { id: string; paused: boolean }) =>
    paused ? api.proxies.pause(id) : api.proxies.resume(id),
  )
}

export function useApplyAllocation() {
  return useProxyMutation((rebalance: boolean) => api.proxies.apply(rebalance))
}

export function useDetachProxy() {
  return useProxyMutation((numberId: string) => api.proxies.detach(numberId))
}

export function useCreateContactShare() {
  return useMutation({
    mutationFn: ({ filters, senderNumberId, destination, confirmRisk }: {
      filters: ContactFilters
      senderNumberId: string
      destination: string
      confirmRisk?: boolean
    }) => api.contacts.share(filters, senderNumberId, destination, confirmRisk),
  })
}

// Enquanto o envio está pendente a tela acompanha o progresso; terminado, para.
export function useContactShareStatus(id: string | null) {
  return useQuery({
    queryKey: ['contact-share', id],
    queryFn: () => api.contacts.shareStatus(id!),
    enabled: !!id,
    refetchInterval: (query) => (query.state.data?.status === 'Pending' ? 2000 : false),
  })
}

export function useExportMetrics() {
  return useQuery({ queryKey: ['export-metrics'], queryFn: api.reports.exportMetrics })
}

export function useAiAnalyses(filters: AiAnalysisFilters, page: number, pageSize = 50) {
  return useQuery({
    queryKey: ['ai-analyses', filters, page, pageSize],
    queryFn: () => api.ai.analyses(filters, page, pageSize),
    placeholderData: (previous) => previous,
  })
}

export function useAiSyntheses(sellerId: string) {
  return useQuery({ queryKey: ['ai-syntheses', sellerId], queryFn: () => api.ai.syntheses(sellerId) })
}

export function useAiLossReasons() {
  return useQuery({ queryKey: ['ai-loss-reasons'], queryFn: api.ai.lossReasons })
}

export function useRunAiAnalyses() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ filters, conversationIds, includeAudio }: {
      filters: AiAnalysisFilters
      conversationIds: string[]
      includeAudio?: boolean
    }) => api.ai.runAnalyses(filters, conversationIds, includeAudio),
    // A rodada nasce ocupando a vaga: a tela precisa travar os botões na hora.
    onSuccess: () => client.invalidateQueries({ queryKey: ['ai-status'] }),
  })
}

export function useRunAiSyntheses() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (filters: AiAnalysisFilters) => api.ai.runSyntheses(filters),
    onSuccess: () => client.invalidateQueries({ queryKey: ['ai-status'] }),
  })
}

export function useAiEstimate() {
  return useMutation({
    mutationFn: ({ kind, filters, includeAudio }: {
      kind: 'Analyze' | 'Synthesize'
      filters: AiAnalysisFilters
      includeAudio?: boolean
    }) => api.ai.estimate(kind, filters, includeAudio),
  })
}

// O estado da vaga única. Enquanto houver rodada em andamento a tela pergunta de
// novo a cada 5s; terminada, as listas são invalidadas para o resultado aparecer
// sem o usuário recarregar.
export function useAiStatus() {
  const client = useQueryClient()
  const wasRunning = useRef(false)

  return useQuery({
    queryKey: ['ai-status'],
    queryFn: async () => {
      const status = await api.ai.status()
      const running = status.running !== null

      if (wasRunning.current && !running) {
        void client.invalidateQueries({ queryKey: ['ai-analyses'] })
        void client.invalidateQueries({ queryKey: ['ai-syntheses'] })
      }

      wasRunning.current = running
      return status
    },
    refetchInterval: (query) => (query.state.data?.running ? 5000 : false),
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

export function useConnectNumber() {
  return useMutation({
    mutationFn: ({
      id,
      confirmBanned,
      confirmCooldown,
    }: {
      id: string
      confirmBanned?: boolean
      confirmCooldown?: boolean
    }) => api.numbers.connect(id, confirmBanned, confirmCooldown),
  })
}

// Pareamento: o número vem do WhatsApp que leu o QR. Enquanto a sessão está
// viva, a tela pergunta o estado a cada 2s — é o servidor que descobre o número.
export function useStartPairing() {
  return useMutation({ mutationFn: (sellerId: string) => api.pairings.start(sellerId) })
}

export function usePairingStatus(id: string | null) {
  const client = useQueryClient()
  return useQuery({
    queryKey: ['pairing', id],
    queryFn: async () => {
      const session = await api.pairings.status(id!)
      if (session.status === 'Completed') {
        void client.invalidateQueries({ queryKey: ['numbers'] })
        void client.invalidateQueries({ queryKey: ['sellers'] })
      }
      return session
    },
    enabled: !!id,
    // A consulta é o sinal de vida da sessão no servidor: parar de perguntar
    // libera a vaga. Por isso vale também enquanto espera a confirmação — sem
    // isso a sessão morreria embaixo de quem está lendo o aviso de transferência.
    refetchInterval: (query) =>
      query.state.data?.status === 'AwaitingScan' || query.state.data?.status === 'AwaitingConfirmation'
        ? 2000
        : false,
  })
}

export function useConfirmPairing() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.pairings.confirm(id),
    onSuccess: () => client.invalidateQueries({ queryKey: ['numbers'] }),
  })
}

// Código de pareamento de um número JÁ cadastrado (reconexão).
export function useNumberPairingCode() {
  return useMutation({
    mutationFn: ({
      id,
      confirmBanned,
      confirmCooldown,
    }: {
      id: string
      confirmBanned?: boolean
      confirmCooldown?: boolean
    }) => api.numbers.pairingCode(id, confirmBanned, confirmCooldown),
  })
}

export function useRequestPairingCode() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, phone }: { id: string; phone: string }) => api.pairings.pairingCode(id, phone),
    // O código volta pela sessão, que é o que a tela mostra.
    onSuccess: (session) => client.setQueryData(['pairing', session.id], session),
  })
}

export function useCancelPairing() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.pairings.cancel(id),
    onSuccess: () => client.invalidateQueries({ queryKey: ['numbers'] }),
  })
}

export function useDisconnectNumber() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: api.numbers.disconnect,
    onSuccess: () => client.invalidateQueries({ queryKey: ['numbers'] }),
  })
}

// Reiniciar não muda status: quem decide é o `connection.update` que vier
// depois, então a lista é invalidada para pegar o que a Evolution reportar.
export function useRestartNumber() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, confirmCooldown }: { id: string; confirmCooldown?: boolean }) =>
      api.numbers.restart(id, confirmCooldown),
    onSuccess: () => client.invalidateQueries({ queryKey: ['numbers'] }),
  })
}

export function useTransferNumber() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, sellerId }: { id: string; sellerId: string }) =>
      api.numbers.transfer(id, sellerId),
    // O número sai de uma lista e entra na outra: as duas precisam ser refeitas.
    onSuccess: () => client.invalidateQueries({ queryKey: ['numbers'] }),
  })
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
