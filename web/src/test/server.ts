import { vi } from 'vitest'

export interface Call {
  method: string
  path: string
  headers: Record<string, string>
  body: unknown
}

type Reply = { status?: number; body?: unknown }
type Handler = Reply | ((call: Call) => Reply)

/**
 * Sustituye fetch por un servidor de mentira. Las rutas se declaran como 'GET /panel/me/' (con la cadena de consulta
 * solo si se quiere afinar). Una ruta sin declarar falla la prueba: así un cambio de endpoint no pasa inadvertido.
 */
export function mockApi(routes: Record<string, Handler>) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost')
      const method = (init?.method ?? 'GET').toUpperCase()
      const call: Call = {
        method,
        path: url.pathname + url.search,
        headers: Object.fromEntries(Object.entries((init?.headers ?? {}) as Record<string, string>)),
        // Los formularios (subida de archivos) llegan como FormData; el resto es JSON.
        body: init?.body instanceof FormData ? init.body : init?.body ? JSON.parse(String(init.body)) : undefined,
      }
      calls.push(call)
      const handler = routes[`${method} ${call.path}`] ?? routes[`${method} ${url.pathname}`]
      if (!handler) throw new Error(`Sin respuesta simulada para ${method} ${call.path}`)
      const reply = typeof handler === 'function' ? handler(call) : handler
      const status = reply.status ?? 200
      if (status === 204) return new Response(null, { status })
      return new Response(JSON.stringify(reply.body ?? {}), { status, headers: { 'Content-Type': 'application/json' } })
    }),
  )
  return {
    calls,
    find: (method: string, pathStart: string) => calls.filter((c) => c.method === method && c.path.startsWith(pathStart)),
  }
}

export const ME = {
  agencyOwner: { id: 'u1', client_id: null, client_name: null, email: 'dueno@pixeldc.test', role: 'agency_owner', two_factor_enabled: false },
  agencyAdmin: { id: 'u2', client_id: null, client_name: null, email: 'equipo@pixeldc.test', role: 'agency_admin', two_factor_enabled: false },
  clientAdmin: { id: 'u3', client_id: 'c1', client_name: 'Don Porko', email: 'admin@donporko.test', role: 'client_admin', two_factor_enabled: false },
  operator: { id: 'u4', client_id: 'c1', client_name: 'Don Porko', email: 'operador@donporko.test', role: 'operator', two_factor_enabled: false },
}
