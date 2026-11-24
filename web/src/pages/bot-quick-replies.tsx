import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { FieldError, notifyError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, get, put } from '@/lib/api'

interface QuickReplies {
  business_name: string | null
  greeting: string
  farewell: string
  closing: string
  defaults: { greeting: string; farewell: string; closing: string }
}

const FIELDS = [
  { key: 'greeting', label: 'Saludo', hint: 'Para abrir la conversación cuando una persona la toma.' },
  { key: 'farewell', label: 'Despedida', hint: 'Para despedirse sin cerrar la conversación.' },
  { key: 'closing', label: 'Cierre (Finalizar)', hint: 'Se envía al pulsar «Finalizar» y la conversación vuelve al chatbot.' },
] as const

/** Así se ve un texto con los datos de ejemplo. */
const preview = (text: string, establishment: string) => text.split('{operador}').join('Camila').split('{establecimiento}').join(establishment || 'tu establecimiento')

/**
 * Respuestas rápidas del chatbot: el guion de saludo, despedida y cierre de quien atiende. Cada chatbot tiene el suyo
 * (un mismo cliente puede tener un restaurante y un supermercado, y no se hablan igual).
 */
export function QuickRepliesPanel({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const q = useQuery({ queryKey: ['bot-quick-replies', botId], queryFn: () => get<{ quick_replies: QuickReplies }>(`/panel/bots/${botId}/quick-replies`) })
  const [name, setName] = useState('')
  const [texts, setTexts] = useState({ greeting: '', farewell: '', closing: '' })
  const [errors, setErrors] = useState<Record<string, string>>({})

  useEffect(() => {
    if (!q.data) return
    const r = q.data.quick_replies
    setName(r.business_name ?? '')
    setTexts({ greeting: r.greeting, farewell: r.farewell, closing: r.closing })
  }, [q.data])

  const save = useMutation({
    mutationFn: () => put(`/panel/bots/${botId}/quick-replies`, { business_name: name, ...texts }),
    onSuccess: () => {
      setErrors({})
      queryClient.invalidateQueries({ queryKey: ['bot-quick-replies', botId] })
      toast.success('Respuestas rápidas guardadas.')
    },
    onError: (e) => {
      if (e instanceof ApiError && e.fields) setErrors(e.fields)
      else notifyError(e)
    },
  })

  if (q.isLoading || !q.data) return <Skeleton className="h-64 w-full" />
  const defaults = q.data.quick_replies.defaults

  return (
    <form
      className="max-w-2xl space-y-5"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      <p className="text-sm text-muted-foreground">
        Quien atiende la Bandeja las usa con un botón. En el texto puedes escribir <code>{'{operador}'}</code> (el nombre de quien atiende) y <code>{'{establecimiento}'}</code> (el nombre de abajo).
        Cada chatbot tiene las suyas.
      </p>
      <div className="space-y-1.5">
        <Label htmlFor="qr-name">Nombre del establecimiento</Label>
        <Input id="qr-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} placeholder="Cómo se llama el negocio en este chatbot (si lo dejas vacío, el de la cuenta)" />
        <FieldError message={errors.business_name} />
      </div>
      {FIELDS.map((f) => (
        <div key={f.key} className="space-y-1.5">
          <div className="flex items-center justify-between gap-2">
            <Label htmlFor={`qr-${f.key}`}>{f.label}</Label>
            <Button type="button" variant="ghost" size="sm" onClick={() => setTexts((t) => ({ ...t, [f.key]: defaults[f.key] }))}>
              Texto sugerido
            </Button>
          </div>
          <Textarea id={`qr-${f.key}`} rows={3} maxLength={600} value={texts[f.key]} onChange={(e) => setTexts((t) => ({ ...t, [f.key]: e.target.value }))} />
          <FieldError message={errors[f.key]} />
          <p className="text-xs text-muted-foreground">{f.hint}</p>
          <p className="rounded-md bg-muted px-3 py-2 text-sm">
            <span className="text-xs text-muted-foreground">Vista previa: </span>
            {preview(texts[f.key], name)}
          </p>
        </div>
      ))}
      <Button type="submit" disabled={save.isPending}>
        Guardar
      </Button>
    </form>
  )
}
