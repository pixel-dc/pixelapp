/**
 * Apariencia del panel elegida por cada usuario. Se guarda en el servidor (le sigue a cualquier dispositivo) y se deja una copia en el
 * navegador solo para pintar sin parpadeo al cargar. Los valores salen de listas fijas: nada de texto libre llega a los estilos.
 */
const HEX = /^#[0-9a-fA-F]{6}$/

export type Background = 'plain' | 'soft' | 'gradient' | 'dots'
export type Radius = 'sm' | 'md' | 'lg'
export type TextSize = 'sm' | 'md' | 'lg'
export type ThemeChoice = 'light' | 'dark' | 'system'

export interface Appearance {
  accent?: string
  background?: Background
  radius?: Radius
  text_size?: TextSize
  theme?: ThemeChoice
}

// Cian de Pixel DC (pixeldc.com): el panel arranca con la marca de la agencia; cada persona puede elegir otro acento en Apariencia.
export const DEFAULT_ACCENT = '#3ad3f8'

let panelAccent: string | undefined
/** Color de la agencia: es el de partida de los usuarios de sus clientes; el que cada usuario elige en su perfil manda sobre él. */
export function setPanelAccent(hex: string | null | undefined) {
  panelAccent = hex && HEX.test(hex) ? hex.toLowerCase() : undefined
}
export const defaultAccent = () => panelAccent ?? DEFAULT_ACCENT

export const BACKGROUNDS: { value: Background; label: string }[] = [
  { value: 'plain', label: 'Liso' },
  { value: 'soft', label: 'Suave' },
  { value: 'gradient', label: 'Degradado' },
  { value: 'dots', label: 'Puntos' },
]
export const RADII: { value: Radius; label: string; css: string }[] = [
  { value: 'sm', label: 'Rectas', css: '0.375rem' },
  { value: 'md', label: 'Medias', css: '0.625rem' },
  { value: 'lg', label: 'Redondas', css: '1rem' },
]
export const TEXT_SIZES: { value: TextSize; label: string; css: string }[] = [
  { value: 'sm', label: 'Pequeño', css: '15px' },
  { value: 'md', label: 'Normal', css: '16px' },
  { value: 'lg', label: 'Grande', css: '17.5px' },
]

const STORAGE_KEY = 'fc-appearance'

const oneOf = <T extends string>(value: unknown, list: readonly { value: T }[]): T | undefined => list.find((i) => i.value === value)?.value

/** Descarta lo que no esté en las listas: un valor raro (del servidor o del navegador) nunca llega a los estilos. */
export function sanitize(input: unknown): Appearance {
  const o = (input && typeof input === 'object' ? input : {}) as Record<string, unknown>
  const out: Appearance = {}
  if (typeof o.accent === 'string' && HEX.test(o.accent)) out.accent = o.accent.toLowerCase()
  const bg = oneOf(o.background, BACKGROUNDS)
  if (bg) out.background = bg
  const radius = oneOf(o.radius, RADII)
  if (radius) out.radius = radius
  const size = oneOf(o.text_size, TEXT_SIZES)
  if (size) out.text_size = size
  if (o.theme === 'light' || o.theme === 'dark' || o.theme === 'system') out.theme = o.theme
  return out
}

/** Negro o blanco, el que mejor se lea sobre el color de acento (luminancia relativa WCAG). */
export function foregroundFor(hex: string): string {
  const channel = (i: number) => {
    const c = Number.parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }
  const luminance = 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2)
  return luminance > 0.4 ? '#111827' : '#ffffff'
}

export function applyAppearance(raw: Appearance | null | undefined, root: HTMLElement = document.documentElement): void {
  const a = sanitize(raw)
  const accent = a.accent ?? defaultAccent()
  root.style.setProperty('--primary', accent)
  root.style.setProperty('--primary-foreground', foregroundFor(accent))
  root.style.setProperty('--ring', accent)
  root.style.setProperty('--sidebar-primary', accent)
  root.style.setProperty('--sidebar-primary-foreground', foregroundFor(accent))
  root.style.setProperty('--sidebar-ring', accent)
  root.style.setProperty('--radius', RADII.find((r) => r.value === (a.radius ?? 'md'))!.css)
  root.style.fontSize = TEXT_SIZES.find((t) => t.value === (a.text_size ?? 'md'))!.css
  root.dataset.bg = a.background ?? 'soft'
}

let editing = false
/** Mientras se edita en la pantalla de Apariencia, lo que llegue del servidor no pisa la vista previa. */
export const setEditingAppearance = (value: boolean) => {
  editing = value
}
export const isEditingAppearance = () => editing

export function readCached(): Appearance {
  try {
    return sanitize(JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}'))
  } catch {
    return {}
  }
}

export function cacheAppearance(a: Appearance): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(sanitize(a)))
  } catch {
    // Sin almacenamiento: la apariencia se pide al servidor en cada carga.
  }
}
