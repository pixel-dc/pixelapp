import { CheckIcon, PipetteIcon } from 'lucide-react'
import { cn } from 'cn'

/** Colores listos para elegir: nadie tiene que saber qué es un hexadecimal. */
const COLOR_PRESETS: { name: string; value: string }[] = [
  { name: 'Cian Pixel DC', value: '#3ad3f8' },
  { name: 'Azul noche Pixel DC', value: '#1e1e2f' },
  { name: 'Lavanda Pixel DC', value: '#cebfff' },
  { name: 'Azul', value: '#2563eb' },
  { name: 'Índigo', value: '#4f46e5' },
  { name: 'Violeta', value: '#7c3aed' },
  { name: 'Rosa', value: '#db2777' },
  { name: 'Rojo', value: '#dc2626' },
  { name: 'Naranja', value: '#ea580c' },
  { name: 'Ámbar', value: '#d97706' },
  { name: 'Verde', value: '#16a34a' },
  { name: 'Turquesa', value: '#0d9488' },
  { name: 'Cian', value: '#0891b2' },
  { name: 'Pizarra', value: '#475569' },
  { name: 'Negro', value: '#111827' },
]

const HEX = /^#[0-9a-fA-F]{6}$/

/**
 * Selector de color con paleta y, si hace falta, cualquier otro color con el selector del navegador.
 * `value` es un #rrggbb o vacío (= el color por defecto); el hexadecimal nunca se le pide al usuario.
 */
export function ColorPicker({ id, value, onChange, fallback = '#2563eb', label = 'Color' }: { id?: string; value: string; onChange: (value: string) => void; fallback?: string; label?: string }) {
  const current = HEX.test(value) ? value.toLowerCase() : ''
  const custom = current && !COLOR_PRESETS.some((c) => c.value === current)
  return (
    <div id={id} role="radiogroup" aria-label={label} className="flex flex-wrap items-center gap-2">
      {COLOR_PRESETS.map((c) => {
        const selected = current === c.value
        return (
          <button
            key={c.value}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-label={c.name}
            title={c.name}
            onClick={() => onChange(c.value)}
            style={{ background: c.value }}
            className={cn('flex size-8 items-center justify-center rounded-full text-white shadow-sm ring-offset-2 ring-offset-background transition hover:scale-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring', selected && 'ring-2 ring-foreground')}
          >
            {selected && <CheckIcon className="size-4" />}
          </button>
        )
      })}
      {/* Otro color: el selector del navegador (rueda o cuentagotas), sin escribir códigos. */}
      <label
        title="Otro color"
        style={custom ? { background: current } : undefined}
        className={cn('relative flex size-8 cursor-pointer items-center justify-center rounded-full border border-dashed text-muted-foreground transition hover:scale-110 focus-within:ring-2 focus-within:ring-ring', custom && 'border-solid text-white ring-2 ring-foreground ring-offset-2 ring-offset-background')}
      >
        {custom ? <CheckIcon className="size-4" /> : <PipetteIcon className="size-4" />}
        <span className="sr-only">Otro color</span>
        <input type="color" value={current || fallback} onChange={(e) => onChange(e.target.value)} className="absolute inset-0 size-full cursor-pointer opacity-0" />
      </label>
      {current && (
        <button type="button" onClick={() => onChange('')} className="text-xs text-muted-foreground underline-offset-4 hover:underline">
          Quitar color
        </button>
      )}
    </div>
  )
}
