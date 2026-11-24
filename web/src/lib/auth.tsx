import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useContext, useEffect, type ReactNode } from 'react'
import { ApiError, get } from './api'
import type { User } from './types'

interface AuthState {
  user: User | null
  loading: boolean
  refresh: () => Promise<unknown>
  /** Dueño o equipo de la agencia: clientes, assets, bots e integraciones. */
  isAgency: boolean
  /** Dueño de la agencia: además, el equipo de la agencia. */
  isAgencyOwner: boolean
  /** Administrador de un cliente: conocimiento, contactos, uso y su equipo. */
  isClientAdmin: boolean
  /** Solo atiende la bandeja. */
  isOperator: boolean
  /** Agencia o administrador del cliente. */
  canManage: boolean
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: ['me'],
    queryFn: async () => {
      try {
        return (await get<{ user: User }>('/panel/me/')).user
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null
        throw e
      }
    },
    retry: false,
    staleTime: 60_000,
  })

  // Si cualquier petición recibe 401 (sesión vencida o revocada), se vuelve a la pantalla de entrada.
  useEffect(() => {
    const onUnauthorized = () => {
      client.setQueryData(['me'], null)
      client.removeQueries({ predicate: (q) => q.queryKey[0] !== 'me' })
    }
    window.addEventListener('px:unauthorized', onUnauthorized)
    return () => window.removeEventListener('px:unauthorized', onUnauthorized)
  }, [client])

  const user = query.data ?? null
  const isAgency = user?.role === 'agency_owner' || user?.role === 'agency_admin'
  return (
    <AuthContext.Provider
      value={{
        user,
        loading: query.isLoading,
        refresh: () => client.invalidateQueries({ queryKey: ['me'] }),
        isAgency,
        isAgencyOwner: user?.role === 'agency_owner',
        isClientAdmin: user?.role === 'client_admin',
        isOperator: user?.role === 'operator',
        canManage: isAgency || user?.role === 'client_admin',
      }}
    >
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth debe usarse dentro de AuthProvider')
  return ctx
}
