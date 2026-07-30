import { useState, type FormEvent } from 'react'
import {
  useAddOutcomeTerm,
  useCreateOutcomeType,
  useDeleteOutcomeType,
  useLabelSuggestions,
  useOutcomeTypes,
  useRemoveOutcomeTerm,
} from '../../api/queries'
import { ApiError } from '../../api/client'
import type { OutcomeTypeDto } from '../../api/types'
import { Button, Card, EmptyState, ErrorState, Input, Select, Spinner } from '../../components/ui'
import { useIsMobile } from '../../lib/useIsMobile'

function TypeCard({
  type,
  onError,
  canDelete,
}: {
  type: OutcomeTypeDto
  onError: (message: string | null) => void
  canDelete: boolean
}) {
  const addTerm = useAddOutcomeTerm()
  const removeTerm = useRemoveOutcomeTerm()
  const deleteType = useDeleteOutcomeType()
  const [term, setTerm] = useState('')

  async function handleAdd(e: FormEvent) {
    e.preventDefault()
    onError(null)
    try {
      await addTerm.mutateAsync({ code: type.code, term: term.trim() })
      setTerm('')
    } catch (err) {
      onError(err instanceof ApiError ? err.message : 'Falha ao adicionar a etiqueta.')
    }
  }

  return (
    <Card data-testid={`type-${type.code}`}>
      <div className="mb-3 flex items-center justify-between gap-2">
        <div>
          <p className="font-semibold">{type.name}</p>
          <p className="text-xs text-ink-muted">código: {type.code}</p>
        </div>
        {canDelete && (
          <Button
            variant="danger"
            onClick={() => {
              if (window.confirm(`Remover o tipo "${type.name}"? As etiquetas dele deixam de contar.`))
                deleteType.mutate(type.code)
            }}
          >
            Remover tipo
          </Button>
        )}
      </div>

      {type.terms.length === 0 ? (
        <p className="text-sm text-ink-muted">Nenhuma etiqueta ainda — nada conta para este tipo.</p>
      ) : (
        <ul className="flex flex-wrap gap-1.5">
          {type.terms.map((t) => (
            <li
              key={t.id}
              // A pilha cresce no celular só para o "✕" ter alvo de dedo.
              className="flex min-h-9 items-center gap-1 rounded-full bg-primary-soft py-1 pl-2.5 text-xs text-primary-strong md:min-h-0 md:gap-1.5 md:pr-2.5"
            >
              {t.term}
              <button
                type="button"
                aria-label={`Remover etiqueta ${t.term}`}
                className="flex h-7 w-7 cursor-pointer items-center justify-center font-bold md:h-auto md:w-auto"
                onClick={() => removeTerm.mutate({ code: type.code, termId: t.id })}
              >
                ✕
              </button>
            </li>
          ))}
        </ul>
      )}

      <form className="mt-3 flex flex-col gap-2 md:flex-row" onSubmit={handleAdd}>
        <Input
          value={term}
          onChange={(e) => setTerm(e.target.value)}
          placeholder="Nova etiqueta (ex.: vendeu)"
          aria-label={`Nova etiqueta para ${type.name}`}
          className="flex-1"
        />
        <Button type="submit" disabled={addTerm.isPending || term.trim().length === 0}>
          Adicionar
        </Button>
      </form>
    </Card>
  )
}

export function LabelsPage() {
  const isMobile = useIsMobile()
  const { data: types, isLoading, isError } = useOutcomeTypes()
  const { data: suggestions } = useLabelSuggestions()
  const createType = useCreateOutcomeType()
  const addTerm = useAddOutcomeTerm()

  const [newTypeName, setNewTypeName] = useState('')
  const [error, setError] = useState<string | null>(null)

  const unmapped = (suggestions ?? []).filter((s) => s.mappedToTypeCode === null)

  async function assignTerm(code: string, term: string) {
    setError(null)
    try {
      await addTerm.mutateAsync({ code, term })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao atribuir a etiqueta.')
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-bold">Etiquetas</h2>
        <p className="text-sm text-ink-muted">
          Escolha quais etiquetas do WhatsApp contam para cada tipo. Alterar aqui{' '}
          <strong>recalcula as conversas já etiquetadas</strong>. Se uma conversa tiver mais de uma
          etiqueta, vale a <strong>última aplicada</strong>.
        </p>
      </div>

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar os tipos de desfecho." />}
      {error && <ErrorState message={error} />}

      <div className="grid gap-4 lg:grid-cols-2">
        {(types ?? []).map((type) => (
          <TypeCard
            key={type.code}
            type={type}
            onError={setError}
            canDelete={type.code !== 'sale'}
          />
        ))}
      </div>

      <Card>
        <h3 className="mb-3 text-sm font-semibold">Novo tipo</h3>
        <form
          className="flex flex-col gap-2 md:flex-row"
          onSubmit={async (e) => {
            e.preventDefault()
            setError(null)
            try {
              await createType.mutateAsync({ code: newTypeName.trim(), name: newTypeName.trim() })
              setNewTypeName('')
            } catch (err) {
              setError(err instanceof ApiError ? err.message : 'Falha ao criar o tipo.')
            }
          }}
        >
          <Input
            value={newTypeName}
            onChange={(e) => setNewTypeName(e.target.value)}
            placeholder="Ex.: Aguardando pagamento"
            aria-label="Nome do novo tipo"
            className="flex-1"
          />
          <Button type="submit" disabled={createType.isPending || newTypeName.trim().length === 0}>
            Criar tipo
          </Button>
        </form>
        <p className="mt-2 text-xs text-ink-muted">
          O tipo novo vira card, coluna e opção de gráfico no dashboard automaticamente.
        </p>
      </Card>

      <Card data-testid="suggestions">
        <h3 className="mb-1 text-sm font-semibold">Etiquetas encontradas no WhatsApp</h3>
        <p className="mb-3 text-xs text-ink-muted">
          Etiquetas que existem nos números conectados e ainda não contam para nenhum tipo.
        </p>

        {unmapped.length === 0 ? (
          <EmptyState message="Nenhuma etiqueta pendente." />
        ) : (
          <ul className="divide-y divide-edge">
            {unmapped.map((s) => (
              <li key={s.labelId} className="flex flex-wrap items-center justify-between gap-2 py-2">
                <div>
                  <span className="text-sm font-medium">{s.name}</span>
                  <span className="ml-2 text-xs text-ink-muted">
                    {s.conversations} {s.conversations === 1 ? 'conversa' : 'conversas'}
                  </span>
                </div>
                {/* Um botão por tipo vira uma parede no celular (são tantos
                    quanto o catálogo tiver): lá a escolha é um <select>. */}
                {isMobile ? (
                  <Select
                    aria-label={`Atribuir a etiqueta ${s.name} a um tipo`}
                    value=""
                    onChange={(e) => {
                      if (e.target.value) void assignTerm(e.target.value, s.name)
                    }}
                    className="w-full"
                  >
                    <option value="">Atribuir a…</option>
                    {(types ?? []).map((type) => (
                      <option key={type.code} value={type.code}>
                        {type.name}
                      </option>
                    ))}
                  </Select>
                ) : (
                  <div className="flex flex-wrap gap-1">
                    {(types ?? []).map((type) => (
                      <Button
                        key={type.code}
                        variant="ghost"
                        onClick={() => void assignTerm(type.code, s.name)}
                      >
                        → {type.name}
                      </Button>
                    ))}
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  )
}
