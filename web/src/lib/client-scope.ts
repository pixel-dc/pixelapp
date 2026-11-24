// Cliente que la agencia eligió en el selector. Viaja en la cabecera X-Client-Id de las rutas de datos;
// el servidor lo valida en cada petición. Un usuario de un cliente nunca lo usa: siempre trabaja en el suyo.

const KEY = 'px:client-scope'

// Lo que es de la persona o de la agencia (sesión, cuenta, equipo de la agencia, configuración) no se filtra por cliente.
const EXCLUDED = ['/panel/auth', '/panel/me', '/panel/setup', '/panel/agency']

let current: string | null = null
try {
  current = localStorage.getItem(KEY)
} catch {
  // Sin almacenamiento (ventana privada): se ve toda la agencia.
}

const listeners = new Set<() => void>()

export const getClientScope = () => current

/** Para que los componentes se repinten cuando cambia la elección (useSyncExternalStore). */
export function subscribeClientScope(listener: () => void) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

export function setClientScope(id: string | null) {
  current = id
  listeners.forEach((l) => l())
  try {
    if (id) localStorage.setItem(KEY, id)
    else localStorage.removeItem(KEY)
  } catch {
    // Sin almacenamiento: la elección vale solo mientras la página siga abierta.
  }
}

const within = (path: string, prefix: string) => path === prefix || path.startsWith(`${prefix}/`) || path.startsWith(`${prefix}?`)

export const scopeAppliesTo = (path: string) => path.startsWith('/panel/') && !EXCLUDED.some((p) => within(path, p))
