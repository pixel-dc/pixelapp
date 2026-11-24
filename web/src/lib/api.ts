// Cliente de la API del panel. Usa la cookie de sesión (HttpOnly) del mismo origen:
// el token nunca pasa por JavaScript. Las peticiones que modifican datos llevan la cabecera anti-CSRF.

import { getClientScope, scopeAppliesTo } from '@/lib/client-scope'

export class ApiError extends Error {
  status: number
  code: string
  fields?: Record<string, string>

  constructor(status: number, code: string, message: string, fields?: Record<string, string>) {
    super(message)
    this.status = status
    this.code = code
    this.fields = fields
  }
}

type Json = Record<string, unknown> | unknown[]

export async function api<T = unknown>(method: string, path: string, body?: Json | FormData): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (method !== 'GET') headers['X-Requested-With'] = 'Pixela'
  const scope = getClientScope()
  if (scope && scopeAppliesTo(path)) headers['X-Client-Id'] = scope
  // Con FormData (subida de archivos) el navegador pone el Content-Type con su separador.
  const isForm = typeof FormData !== 'undefined' && body instanceof FormData
  if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json'

  let response: Response
  try {
    response = await fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : isForm ? (body as FormData) : JSON.stringify(body),
      credentials: 'same-origin',
    })
  } catch {
    throw new ApiError(0, 'network', 'No se pudo conectar con el servidor.')
  }

  if (response.status === 204) return undefined as T

  const text = await response.text()
  let data: Record<string, unknown> = {}
  try {
    data = text ? (JSON.parse(text) as Record<string, unknown>) : {}
  } catch {
    // Respuesta que no es JSON (p. ej. un error del proxy): se trata como error genérico.
  }

  if (!response.ok) {
    const error = (data.error ?? {}) as { code?: string; message?: string; fields?: Record<string, string> }
    if (response.status === 401 && !path.startsWith('/panel/auth/')) {
      window.dispatchEvent(new Event('px:unauthorized'))
    }
    // El 429 del limitador de ritmo no trae cuerpo; un 429 con mensaje es un tope del cliente y se muestra tal cual.
    if (response.status === 429 && !error.message) {
      throw new ApiError(429, 'rate_limited', 'Demasiados intentos. Espera un momento.')
    }
    throw new ApiError(response.status, error.code ?? 'error', error.message ?? 'Ocurrió un error inesperado.', error.fields)
  }

  return data as T
}

export const get = <T,>(path: string) => api<T>('GET', path)
export const post = <T,>(path: string, body?: Json) => api<T>('POST', path, body ?? {})
export const put = <T,>(path: string, body: Json) => api<T>('PUT', path, body)
export const patch = <T,>(path: string, body: Json) => api<T>('PATCH', path, body)
export const del = <T = void,>(path: string) => api<T>('DELETE', path)
export const postForm = <T,>(path: string, form: FormData) => api<T>('POST', path, form)
