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
import { Button, Card, EmptyState, ErrorState, Input, Spinner } from '../../components/ui'

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
              className="flex items-center gap-1.5 rounded-full bg-primary-soft px-2.5 py-1 text-xs text-primary-strong"
            >
              {t.term}
              <button
                type="button"
                aria-label={`Remover etiqueta ${t.term}`}
                className="cursor-pointer font-bold"
                onClick={() => removeTerm.mutate({ code: type.code, termId: t.id })}
              >
                ✕
              </button>
            </li>
          ))}
        </ul>
      )}

      <form className="mt-3 flex gap-2" onSubmit={handleAdd}>
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
  const { data: types, isLoading, isError } = useOutcomeTypes()
  const { data: suggestions } = useLabelSuggestions()
  const createType = useCreateOutcomeType()
  const addTerm = useAddOutcomeTerm()

  const [newTypeName, setNewTypeName] = useState('')
  const [error, setError] = useState<string | null>(null)

  const unmapped = (suggestions ?? []).filter((s) => s.mappedToTypeCode === null)

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
          className="flex gap-2"
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
                <div className="flex flex-wrap gap-1">
                  {(types ?? []).map((type) => (
                    <Button
                      key={type.code}
                      variant="ghost"
                      onClick={async () => {
                        setError(null)
                        try {
                          await addTerm.mutateAsync({ code: type.code, term: s.name })
                        } catch (err) {
                          setError(err instanceof ApiError ? err.message : 'Falha ao atribuir a etiqueta.')
                        }
                      }}
                    >
                      → {type.name}
                    </Button>
                  ))}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  )
}
