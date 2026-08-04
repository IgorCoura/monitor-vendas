import type { MetricsDto } from '../api/types'

// Textos de ajuda exibidos no "?" ao lado de cada métrica. Espelham as regras
// do MetricsCalculator do server — se a regra mudar lá, atualize aqui.
export const metricHelp = {
  conversas:
    'Conversas cuja primeira mensagem veio do cliente, iniciadas no período. Conversa nova = primeira mensagem após 15 dias de silêncio entre o número e o contato.',
  resposta:
    'Das conversas iniciadas pelo cliente, % que recebeu ao menos uma resposta do vendedor em até 24 horas úteis (seg–sex 9h–18h, sáb 9h–13h, descontando feriados e quedas do número).',
  naoRespondidas:
    'Conversas iniciadas pelo cliente que não receberam resposta dentro da janela de 24 horas úteis.',
  disparos: 'Conversas iniciadas pelo vendedor (primeira mensagem é dele).',
  captacoes: 'Disparos que obtiveram alguma resposta do cliente.',
  primeiraResposta:
    'Mediana do tempo útil entre a primeira mensagem do cliente e a primeira resposta do vendedor. Em períodos de até 7 dias o valor é exato; em períodos maiores é estimado (o servidor guarda faixas de tempo, não a lista completa).',
  espera:
    'Tempo útil que o cliente esperou até ser respondido, considerando TODA mensagem do cliente que recebeu resposta (não só a primeira da conversa). O valor grande é a média; mín/máx no detalhe.',
  vendas:
    'Conversas marcadas com a etiqueta "venda" no WhatsApp Business dentro do período.',
  conversao: 'Vendas ÷ conversas atendidas.',
  followUp:
    'Entre os silêncios de 24+ horas úteis dentro das conversas, % em que quem retomou o contato foi o vendedor. Conta cada silêncio (a mesma conversa pode esfriar e ser resgatada mais de uma vez).',
  mediaEnviadas: 'Mensagens enviadas ÷ horas úteis do período (descontando quedas do número).',
  mediaRecebidas: 'Mensagens recebidas ÷ horas úteis do período (descontando quedas do número).',
  fechamento: 'Tempo útil médio entre o início da conversa e a marcação da venda.',
  uptime: 'Percentual do tempo do período em que o número esteve conectado.',
  bans: 'Quantas vezes o número entrou em estado banido (statusReason 403) no período.',
  ultimoEnvio: 'Data e hora da última mensagem enviada pelo vendedor no período.',
  msgsEnviadas: 'Total de mensagens enviadas pelo vendedor no período.',
  msgsRecebidas: 'Total de mensagens recebidas de clientes no período.',
  taxaLeitura: 'Percentual das mensagens enviadas que o cliente leu.',
} as const

// Semáforo de saúde do número — espelha o NumberHealth do server.
export const healthLevelLabel = {
  NoData: 'Saúde: sem dados',
  Low: 'Saúde: ok',
  Medium: 'Saúde: atenção',
  High: 'Saúde: risco',
  Critical: 'Saúde: crítico',
} as const

// Rótulo curto de cada sinal que pesou no score (o valor vem do servidor).
export const healthSignalLabel: Record<string, string> = {
  delivery: 'Taxa de entrega',
  response: 'Taxa de resposta',
  outboundShare: 'Conversas iniciadas por nós',
  disconnections: 'Desconexões nas últimas 24h',
  newContactsPerDay: 'Novos contatos por dia',
  sendRestriction: 'Restrição de envio do WhatsApp',
  ban: 'Bans no período',
}

export const healthHelp =
  'Score de risco de banimento (0–100) dos últimos 7 dias, montado com sinais medidos: ' +
  'taxa de entrega (mensagem enviada que nunca chega é o aviso clássico de restrição), ' +
  'taxa de resposta, proporção de conversas iniciadas por nós, desconexões, restrição de ' +
  'envio (erro 463) e bans. "Sem dados" = número sem tráfego no período.'

// Ajuda dos tipos de desfecho: o texto é montado a partir do nome do tipo, já que
// o catálogo é configurável na tela de Etiquetas.
export function outcomeHelp(name: string): string {
  return `Conversas marcadas como "${name}" pelas etiquetas do WhatsApp configuradas na tela Etiquetas. Se a conversa tiver mais de uma etiqueta, vale a última aplicada.`
}

// Métricas disponíveis nos gráficos do dashboard. A ordem define qual métrica
// um gráfico novo assume (a primeira ainda não usada).
export interface ChartMetricDef {
  key: string
  label: string
  percent: boolean
  decimals?: number
  value: (m: MetricsDto) => number
}

export const chartMetrics: ChartMetricDef[] = [
  { key: 'conversion', label: 'Conversão', percent: true, value: (m) => (m.conversionRate ?? 0) * 100 },
  { key: 'response', label: 'Taxa de resposta', percent: true, value: (m) => (m.responseRate ?? 0) * 100 },
  { key: 'sales', label: 'Vendas', percent: false, value: (m) => m.sales },
  { key: 'shots', label: 'Disparos', percent: false, value: (m) => m.outboundConversationsStarted },
  { key: 'captures', label: 'Captações', percent: false, value: (m) => m.outboundConversationsEngaged },
  { key: 'avgSent', label: 'Média env./h', percent: false, decimals: 1, value: (m) => m.avgSentPerBusinessHour ?? 0 },
]

export function outcomeCount(metrics: MetricsDto, typeCode: string): number {
  return metrics.outcomes?.find((o) => o.typeCode === typeCode)?.count ?? 0
}

// Tipos de desfecho viram métrica de gráfico automaticamente (venda já está na
// lista fixa como "Vendas"; os demais entram conforme o catálogo do servidor).
export function chartMetricsFor(outcomeTypes: { typeCode: string; name: string }[]): ChartMetricDef[] {
  const dynamic = outcomeTypes
    .filter((t) => t.typeCode !== 'sale')
    .map<ChartMetricDef>((t) => ({
      key: `outcome:${t.typeCode}`,
      label: t.name,
      percent: false,
      value: (m) => outcomeCount(m, t.typeCode),
    }))

  return [...chartMetrics, ...dynamic]
}

export function chartMetricByKey(key: string, all: ChartMetricDef[] = chartMetrics): ChartMetricDef {
  return all.find((m) => m.key === key) ?? all[0]
}

// Valida a lista de gráficos vinda do localStorage: descarta chaves que não
// existem mais, garante a exclusividade (uma métrica por gráfico) e nunca
// devolve lista vazia.
export function sanitizeChartKeys(value: unknown, all: ChartMetricDef[] = chartMetrics): string[] {
  const raw = Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : []
  const valid = [...new Set(raw)].filter((key) => all.some((m) => m.key === key))
  return valid.length > 0 ? valid : [chartMetrics[0].key]
}

export function formatChartValue(def: ChartMetricDef, value: number): string {
  if (def.percent) return `${value.toFixed(1)}%`
  return def.decimals ? value.toFixed(def.decimals) : String(Math.round(value))
}
