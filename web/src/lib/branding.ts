import { useQuery } from '@tanstack/react-query'

/** Marca del acceso por el que se entra: el de un cliente (su subdominio) o el panel general de la agencia. */
export interface Branding {
  name: string
  /** Cliente dueño del subdominio; null en el panel general. */
  client_id: string | null
  slug: string | null
  color: string | null
  icon_url: string
  /** Dominio bajo el que cuelga el acceso de cada cliente (`{slug}.{app_domain}`); null si no está configurado. */
  app_domain: string | null
}

const FALLBACK: Branding = { name: 'Pixelapp', client_id: null, slug: null, color: null, icon_url: '/pixela-icon.png', app_domain: null }

/** Se pide una vez: no cambia mientras la página siga abierta. Si falla, se ve como el panel general. */
export function useBranding(): Branding {
  const { data } = useQuery({
    queryKey: ['branding'],
    queryFn: async () => {
      const response = await fetch('/panel/branding', { headers: { Accept: 'application/json' }, credentials: 'same-origin' })
      return response.ok ? ((await response.json()) as Branding) : FALLBACK
    },
    staleTime: Infinity,
    retry: false,
  })
  return data ?? FALLBACK
}

/** La dirección del acceso de un cliente (lo que la agencia le comparte); null si no hay dominio configurado. */
export const clientAppUrl = (branding: Branding, slug: string) => (branding.app_domain ? `https://${slug}.${branding.app_domain}` : null)
