import { useEffect, useMemo, useState } from 'react'
import clsx from 'clsx'
import { ApiError } from '../../api/client'
import {
  useAiAnalyses,
  useAiJob,
  useAiLossReasons,
  useAiSyntheses,
  useOutcomeTypes,
  useRunAiAnalyses,
  useRunAiSyntheses,
  useSellers,
} from '../../api/queries'
import type { AiAnalysisFilters, AiAnalysisRowDto } from '../../api/types'
import { Button, Card, EmptyState, ErrorState, Input, Spinner } from '../../components/ui'
import { dayEndIso, dayStartIso, fmtDateTime, toDayInput } from '../../lib/format'

const PAGE_SIZE = 50

function brl(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' })
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-xs font-medium text-ink-muted">
      {label}
      {children}
    </label>
  )
}

function Select({
  label,
  value,
  onChange,
  children,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  children: React.ReactNode
}) {
  return (
    <Field label={label}>
      <select
        aria-label={label}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-lg border border-edge bg-card px-3 py-1.5 text-sm text-ink focus:border-primary focus:outline-none"
      >
        {children}
      </select>
    </Field>
  )
}

function Detail({ row }: { row: AiAnalysisRowDto }) {
  return (
    <tr className="border-b border-edge/60 bg-surface/60">
      <td colSpan={8} className="px-4 py-3 text-xs text-ink-muted">
        <div className="grid gap-2 md:grid-cols-2">
          {row.evidence && (
            <p>
              <strong className="text-ink">Evidência:</strong> “{row.evidence}”
            </p>
          )}
          {row.objections && (
            <p>
              <strong className="text-ink">Objeções:</strong> {row.objections}
            </p>
          )}
          {row.interest && (
            <p>
              <strong className="text-ink">Interesse:</strong> {row.interest}
            </p>
          )}
          <p>
            <strong className="text-ink">Pediu a venda:</strong> {row.askedForSale ? 'sim' : 'não'} ·{' '}
            <strong className="text-ink">Sinal ignorado:</strong> {row.ignoredBuyingSignal ? 'sim' : 'não'}
          </p>
          {row.shouldRecontact && row.recontactReason && (
            <p>
              <strong className="text-ink">Recontatar:</strong> {row.recontactReason}
            </p>
          )}
          {row.suggestedMessage && (
            <p>
              <strong className="text-ink">Mensagem sugerida:</strong> {row.suggestedMessage}
            </p>
          )}
          {row.conductAlert && (
            <p className="text-danger">
              <strong>Alerta de conduta:</strong> {row.conductAlert}
            </p>
          )}
          <p>
            Lido por {row.model} em {fmtDateTime(row.analyzedAt)}
            {row.versions > 1 && ` · ${row.versions} versões`}
          </p>
        </div>
      </td>
    </tr>
  )
}

export function AiAnalysisPage() {
  const now = useMemo(() => new Date(), [])
  const [from, setFrom] = useState(() => toDayInput(new Date(now.getTime() - 30 * 86_400_000)))
  const [to, setTo] = useState(() => toDayInput(now))
  const [sellerId, setSellerId] = useState('')
  const [status, setStatus] = useState('')
  const [lossReason, setLossReason] = useState('')
  const [divergent, setDivergent] = useState<AiAnalysisFilters['divergent']>('')
  const [recontact, setRecontact] = useState<AiAnalysisFilters['recontact']>('')
  const [page, setPage] = useState(1)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const [includeAudio, setIncludeAudio] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const filters: AiAnalysisFilters = useMemo(
    () => ({
      from: dayStartIso(from),
      to: dayEndIso(to),
      sellerId,
      status,
      lossReason,
      divergent,
      recontact,
    }),
    [from, to, sellerId, status, lossReason, divergent, recontact],
  )

  useEffect(() => setPage(1), [filters])

  const { data, isLoading, isError } = useAiAnalyses(filters, page, PAGE_SIZE)
  const { data: syntheses } = useAiSyntheses(sellerId)
  const { data: sellers } = useSellers()
  const { data: types } = useOutcomeTypes()
  const { data: lossReasons } = useAiLossReasons()
  const { data: job } = useAiJob(jobId)

  const runAnalyses = useRunAiAnalyses()
  const runSyntheses = useRunAiSyntheses()
  const running = job?.status === 'Pending' || job?.status === 'Running'

  async function start(kind: 'analyses' | 'syntheses') {
    setError(null)
    try {
      const created =
        kind === 'analyses'
          ? await runAnalyses.mutateAsync({ filters, conversationIds: [], includeAudio })
          : await runSyntheses.mutateAsync(filters)
      setJobId(created.id)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Não foi possível iniciar.')
    }
  }

  const total = data?.total ?? 0

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-xl font-bold">Análises por IA</h2>
          <p className="max-w-2xl text-sm text-ink-muted">
            Leitura das conversas feita por um modelo de linguagem — <strong>estimativa, não medição</strong>.
            O desfecho oficial continua sendo o da etiqueta; a coluna de divergência mostra onde os dois
            discordam.
          </p>
        </div>
        <div className="flex flex-col items-end gap-2">
          <div className="flex items-center gap-2">
            <Button variant="ghost" onClick={() => start('analyses')} disabled={running}>
              Analisar conversas
            </Button>
            <Button onClick={() => start('syntheses')} disabled={running}>
              Refazer síntese
            </Button>
          </div>
          <label className="flex items-center gap-2 text-xs text-ink-muted">
            <input
              type="checkbox"
              checked={includeAudio}
              onChange={(e) => setIncludeAudio(e.target.checked)}
              aria-label="Enviar áudios das conversas"
            />
            Enviar áudios (a voz do cliente vai para a IA)
          </label>
        </div>
      </div>

      {error && <ErrorState message={error} />}

      {job && (
        <Card data-testid="ai-job" className="flex items-center gap-3 py-3">
          {running && <Spinner />}
          <div className="text-sm">
            {running && (
              <span>
                {job.kind === 'Analyze' ? 'Analisando conversas' : 'Refazendo sínteses'}…{' '}
                {job.processed}/{job.total}
              </span>
            )}
            {job.status === 'Completed' && (
              <span>
                Concluído: {job.processed} processadas
                {job.skipped > 0 && `, ${job.skipped} sem análise`} · custo {brl(job.costBrl)}
              </span>
            )}
            {job.status === 'Failed' && <span className="text-danger">{job.error ?? 'Falhou.'}</span>}
          </div>
          {running && (
            <span className="text-xs text-ink-muted">
              Pode levar minutos por causa do limite de chamadas da IA.
            </span>
          )}
        </Card>
      )}

      <Card className="flex flex-wrap items-end gap-4">
        <Field label="De">
          <Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="De" />
        </Field>
        <Field label="Até">
          <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} aria-label="Até" />
        </Field>
        <Select label="Vendedor" value={sellerId} onChange={setSellerId}>
          <option value="">Todos</option>
          {(sellers ?? []).map((seller) => (
            <option key={seller.id} value={seller.id}>
              {seller.name}
            </option>
          ))}
        </Select>
        <Select label="Status da IA" value={status} onChange={setStatus}>
          <option value="">Todos</option>
          <option value="open">Em andamento</option>
          {(types ?? []).map((type) => (
            <option key={type.code} value={type.code}>
              {type.name}
            </option>
          ))}
        </Select>
        <Select label="Motivo da perda" value={lossReason} onChange={setLossReason}>
          <option value="">Todos</option>
          {(lossReasons ?? []).map((reason) => (
            <option key={reason.code} value={reason.code}>
              {reason.label}
            </option>
          ))}
        </Select>
        <Select
          label="Divergência"
          value={divergent}
          onChange={(value) => setDivergent(value as AiAnalysisFilters['divergent'])}
        >
          <option value="">Todas</option>
          <option value="true">Só divergentes</option>
          <option value="false">Só concordantes</option>
        </Select>
        <Select
          label="Recontatar"
          value={recontact}
          onChange={(value) => setRecontact(value as AiAnalysisFilters['recontact'])}
        >
          <option value="">Todos</option>
          <option value="true">Só a recontatar</option>
          <option value="false">Sem recontato</option>
        </Select>
      </Card>

      {(syntheses ?? []).length > 0 && (
        <div className="grid gap-4 md:grid-cols-2" data-testid="ai-syntheses">
          {(syntheses ?? []).map((synthesis) => (
            <Card key={synthesis.sellerId} className="space-y-2">
              <div className="flex items-center justify-between gap-2">
                <h3 className="font-semibold">{synthesis.sellerName}</h3>
                {synthesis.stale && (
                  <span className="rounded-full bg-warn-soft px-2 py-0.5 text-xs font-medium text-ink">
                    Desatualizada
                  </span>
                )}
              </div>
              {synthesis.overview && <p className="text-sm text-ink-muted">{synthesis.overview}</p>}
              {synthesis.strengths.length > 0 && (
                <div className="text-sm">
                  <p className="text-xs font-medium text-ink-muted">Pontos fortes</p>
                  <ul className="list-inside list-disc">
                    {synthesis.strengths.map((item) => (
                      <li key={item}>{item}</li>
                    ))}
                  </ul>
                </div>
              )}
              {synthesis.improvements.length > 0 && (
                <div className="text-sm">
                  <p className="text-xs font-medium text-ink-muted">A melhorar</p>
                  <ul className="list-inside list-disc">
                    {synthesis.improvements.map((item) => (
                      <li key={item}>{item}</li>
                    ))}
                  </ul>
                </div>
              )}
              <p className="text-xs text-ink-muted">
                {synthesis.conversationsCount} conversas · {fmtDateTime(synthesis.createdAt)}
              </p>
            </Card>
          ))}
        </div>
      )}

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar as análises." />}
      {data && data.items.length === 0 && (
        <EmptyState message="Nenhuma conversa analisada com esses filtros. Use “Analisar conversas” para ler as do período." />
      )}

      {data && data.items.length > 0 && (
        <Card className="overflow-x-auto p-0">
          <table data-testid="analyses-table" className="w-full text-sm">
            <thead>
              <tr className="border-b border-edge text-left text-xs uppercase tracking-wide text-ink-muted">
                <th className="px-4 py-3">Cliente</th>
                <th className="px-4 py-3">Vendedor</th>
                <th className="px-4 py-3">Última mensagem</th>
                <th className="px-4 py-3">Etiqueta</th>
                <th className="px-4 py-3">Status (IA)</th>
                <th className="px-4 py-3">Divergência</th>
                <th className="px-4 py-3">Motivo</th>
                <th className="px-4 py-3">Resumo</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((row) => (
                <>
                  <tr
                    key={row.analysisId}
                    onClick={() => setExpanded(expanded === row.analysisId ? null : row.analysisId)}
                    className="cursor-pointer border-b border-edge/60 last:border-0 hover:bg-surface"
                  >
                    <td className="px-4 py-3 font-medium">{row.contactName}</td>
                    <td className="px-4 py-3 text-ink-muted">{row.sellerName}</td>
                    <td className="px-4 py-3 text-ink-muted">{fmtDateTime(row.lastMessageAt)}</td>
                    <td className="px-4 py-3">{row.realOutcome ?? '—'}</td>
                    <td className="px-4 py-3">
                      {row.aiStatus}
                      {row.confidence < 0.5 && (
                        <span className="ml-1 text-xs text-ink-muted">(confiança baixa)</span>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <span
                        className={clsx(
                          'rounded-full px-2 py-0.5 text-xs font-medium',
                          row.divergent ? 'bg-danger-soft text-ink' : 'text-ink-muted',
                        )}
                      >
                        {row.divergent ? 'Sim' : 'Não'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-ink-muted">{row.lossReason ?? '—'}</td>
                    <td className="px-4 py-3 text-ink-muted">{row.summary ?? '—'}</td>
                  </tr>
                  {expanded === row.analysisId && <Detail key={`${row.analysisId}-detail`} row={row} />}
                </>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-sm text-ink-muted">
          <span>
            {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, total)} de {total}
          </span>
          <div className="flex gap-2">
            <Button variant="ghost" disabled={page === 1} onClick={() => setPage((p) => p - 1)}>
              Anterior
            </Button>
            <Button
              variant="ghost"
              disabled={page * PAGE_SIZE >= total}
              onClick={() => setPage((p) => p + 1)}
            >
              Próxima
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
