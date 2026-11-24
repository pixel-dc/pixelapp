import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useSyncExternalStore } from 'react'
import { useNavigate } from 'react-router'
import { getClientScope, setClientScope, subscribeClientScope } from '@/lib/client-scope'

interface ClientOption {
  id: string
  name: string
}

export const useClientScope = () => useSyncExternalStore(subscribeClientScope, getClientScope)

/** Lista de clientes para el selector. Se pide sin cliente elegido (la agencia ve todos). */
export function useClientOptions(enabled = true) {
  return useQuery({
    queryKey: ['client-options'],
    queryFn: async () => {
      // Sin la cabecera del selector: la lista siempre es la de toda la agencia.
      const response = await fetch('/panel/clients', { headers: { Accept: 'application/json' }, credentials: 'same-origin' })
      if (!response.ok) return { clients: [] as ClientOption[] }
      return (await response.json()) as { clients: ClientOption[] }
    },
    staleTime: 60_000,
    enabled,
  })
}

/** Selector de la agencia: con qué cliente trabaja en las pantallas de datos. «Todos los clientes» = vista de agencia. */
export function ClientScopeSelector() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const clients = useClientOptions().data?.clients
  const selected = useClientScope() ?? ''

  // Si el cliente guardado ya no existe, se vuelve a la vista de todos.
  useEffect(() => {
    if (clients && selected && !clients.some((c) => c.id === selected)) {
      setClientScope(null)
      void queryClient.invalidateQueries()
    }
  }, [clients, selected, queryClient])

  if (!clients?.length) return null

  const change = (id: string) => {
    setClientScope(id || null)
    // Las pantallas abiertas eran de otro alcance: se pide todo de nuevo.
    void queryClient.invalidateQueries({ predicate: (q) => q.queryKey[0] !== 'client-options' && q.queryKey[0] !== 'me' })
    navigate('/')
  }

  return (
    <div className="px-2 pb-1">
      <label htmlFor="client-scope" className="mb-1 block text-xs text-sidebar-foreground/70">
        Estás viendo
      </label>
      <select
        id="client-scope"
        className="h-9 w-full rounded-md border border-sidebar-border bg-sidebar px-2 text-sm text-sidebar-foreground"
        value={selected}
        onChange={(e) => change(e.target.value)}
      >
        <option value="">Todos los clientes</option>
        {clients.map((c) => (
          <option key={c.id} value={c.id}>
            {c.name}
          </option>
        ))}
      </select>
    </div>
  )
}

/** Aviso fijo cuando la agencia trabaja dentro de un cliente: lo que cambie aquí cambia en ese cliente. */
export function ClientScopeBanner() {
  const selected = useClientScope()
  const name = useClientOptions().data?.clients.find((c) => c.id === selected)?.name
  const queryClient = useQueryClient()
  if (!selected || !name) return null
  return (
    <div role="status" className="flex flex-wrap items-center justify-between gap-2 bg-primary/15 px-4 py-2 text-sm">
      <span>
        Estás trabajando en <strong>{name}</strong>. Lo que cambies aquí queda en ese cliente.
      </span>
      <button
        type="button"
        className="underline underline-offset-2"
        onClick={() => {
          setClientScope(null)
          void queryClient.invalidateQueries({ predicate: (q) => q.queryKey[0] !== 'client-options' && q.queryKey[0] !== 'me' })
        }}
      >
        Ver todos los clientes
      </button>
    </div>
  )
}
