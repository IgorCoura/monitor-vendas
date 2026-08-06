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
    'Tempo útil que o cliente esperou até ser respondido. Cada resposta do vendedor fecha a espera aberta pela primeira mensagem do cliente ainda sem resposta; mensagens seguidas do vendedor não contam de novo, e disparo não conta. Tempo fora do expediente não entra. O período combina os dias: mín e máx são o menor e o maior do período, e a média é a média das médias de cada dia.',
  vendas:
    'Conversas marcadas com a etiqueta "venda" no WhatsApp Business dentro do período.',
  conversao: 'Vendas ÷ conversas atendidas.',
  followUp:
    'Entre os silêncios de 24+ horas úteis dentro das conversas, % em que quem retomou o contato foi o vendedor. Conta cada silêncio (a mesma conversa pode esfriar e ser resgatada mais de uma vez).',
  mediaEnviadas: 'Mensagens enviadas ÷ horas úteis do período (descontando quedas do número).',
  mediaRecebidas: 'Mensagens recebidas ÷ horas úteis do período (descontando quedas do número).',
  fechamento: 'Tempo útil médio entre o início da conversa e a marcação da venda.',
  uptime:
    'Percentual do tempo em que os números do vendedor estiveram conectados. A conta é sobre o tempo em que cada número existia e era dele: 100% só aparece quando TODOS ficaram no ar o período inteiro, e "—" quando não há número a medir.',
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

// Tela de proxies — cada métrica exibida leva o "?" com um destes textos.
export const proxyHelp = {
  ativos:
    'Proxies contratados que estão saudáveis e recebendo números novos. Pausados, suspeitos, vencidos e revogados não entram.',
  atribuidos: 'Números que hoje saem por um proxy dedicado em vez do IP do servidor.',
  semProxy:
    'Números que não couberam em nenhum proxy (capacidade esgotada) ou foram criados com o uso de proxies desligado. Contrate mais proxies no portal do fornecedor e clique em "Distribuir números".',
  ocupacao:
    'Números neste proxy sobre a capacidade dele. A capacidade vem do limite de dispositivos do plano, de um ajuste manual, ou do padrão configurado — nessa ordem.',
  vendedores:
    'Quantos vendedores distintos têm número neste proxy. O algoritmo evita concentrar os números de um mesmo vendedor num proxy só: se o IP queimar, o vendedor não fica inteiro fora do ar.',
  bans:
    'Bans (statusReason 403) ocorridos no período enquanto o número estava neste proxy. O vínculo é histórico: mover um número depois não muda o ban do mês passado.',
  numerosBanidos: 'Quantos números distintos foram banidos neste proxy no período.',
} as const

// Tela de aquecimento. Os textos espelham WarmupPlan e WarmupExecutor no server;
// mudou a regra lá, atualize aqui.
export const warmupHelp = {
  pool: 'Números que participam do aquecimento agora. Entrar é decisão de quem opera: nenhum número entra sozinho.',
  mensagens: 'Mensagens do aquecimento enviadas hoje, somando todos os números do pool.',
  conversas: 'Conversas do aquecimento criadas hoje. Cada uma tem de 3 a 7 mensagens, espalhadas ao longo de minutos ou horas.',
  entrega:
    'Parte das mensagens do pool que o WhatsApp confirmou ter entregue (só as com mais de 15 minutos). Mensagem que não chega é o primeiro sinal de restrição: abaixo de 60%, com pelo menos 20 mensagens na amostra, o aquecimento para sozinho.',
  meta:
    'Quantas mensagens este número deveria ter hoje para parecer ativo, sorteada entre 20 e 40 a cada dia. É PISO, não teto: o aquecimento completa só o que a conversa com aluno de verdade não cobriu.',
  circulo:
    'Colegas com quem este número conversa. O círculo próximo fala quase todo dia; os ocasionais, a cada uma ou duas semanas. Ele cresce um colega por semana desde a entrada no pool — quatro amizades no primeiro dia é o que denuncia grafo desenhado.',
  capado:
    'A meta foi reduzida pela capacidade do grafo: com poucos colegas, atingir 20–40 mensagens exigiria repetir o mesmo par o dia inteiro. O teto sobe sozinho conforme mais números entram no pool.',
  real: 'Mensagens que este número enviou hoje para clientes de verdade. Elas abatem a meta do aquecimento.',
} as const

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
