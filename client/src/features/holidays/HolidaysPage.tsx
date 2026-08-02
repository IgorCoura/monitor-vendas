import { useState, type FormEvent } from 'react'
import { useCreateHoliday, useDeleteHoliday, useHolidays } from '../../api/queries'
import { ApiError } from '../../api/client'
import { Button, Card, EmptyState, ErrorState, Input, Spinner } from '../../components/ui'
import { fmtDate } from '../../lib/format'

export function HolidaysPage() {
  const { data: holidays, isLoading, isError } = useHolidays()
  const createHoliday = useCreateHoliday()
  const deleteHoliday = useDeleteHoliday()

  const [date, setDate] = useState('')
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      await createHoliday.mutateAsync({ date, name: name.trim() })
      setDate('')
      setName('')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Falha ao cadastrar o feriado.')
    }
  }

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <h2 className="text-xl font-bold">Feriados</h2>
        <p className="text-sm text-ink-muted">
          Dias cadastrados aqui ficam fora do relógio útil das métricas.
        </p>
      </div>

      <Card>
        <form className="flex flex-col gap-2 md:flex-row md:flex-wrap" onSubmit={handleSubmit}>
          <Input
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
            aria-label="Data do feriado"
            required
          />
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Nome do feriado"
            aria-label="Nome do feriado"
            className="flex-1"
            required
          />
          <Button type="submit" loading={createHoliday.isPending}>
            Adicionar feriado
          </Button>
        </form>
        {error && <p className="mt-2 text-xs text-danger">{error}</p>}
      </Card>

      {isLoading && <Spinner />}
      {isError && <ErrorState message="Não foi possível carregar os feriados." />}
      {holidays?.length === 0 && <EmptyState message="Nenhum feriado cadastrado." />}

      {holidays && holidays.length > 0 && (
        <Card>
          <ul className="divide-y divide-edge">
            {holidays.map((holiday) => (
              <li key={holiday.id} className="flex items-center justify-between gap-2 py-2">
                <div className="min-w-0">
                  <span className="font-medium">{fmtDate(holiday.date)}</span>
                  <span className="ml-3 text-sm text-ink-muted">{holiday.name}</span>
                </div>
                <Button
                  variant="danger"
                  onClick={() => {
                    if (window.confirm(`Remover o feriado "${holiday.name}"?`))
                      deleteHoliday.mutate(holiday.id)
                  }}
                >
                  Remover
                </Button>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  )
}
