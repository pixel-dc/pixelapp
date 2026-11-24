import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { toast } from 'sonner'
import { ColorPicker } from '@/components/color-picker'
import { FieldError, NativeSelect, PageHeader } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, get, put } from '@/lib/api'
import { type AgencySettings, WIDGET_TEXTS } from '@/lib/types'

/** Configuración general de la agencia: lo que antes vivía en variables de entorno, ahora editable aquí. */
export default function AgencySettingsPage() {
  const settings = useQuery({ queryKey: ['agency-settings'], queryFn: () => get<{ settings: AgencySettings }>('/panel/agency/settings') })
  return (
    <>
      <PageHeader title="Configuración" description="Modelo de Claude, precios de referencia, retención, dirección pública y marca por defecto del widget." />
      {settings.data ? <SettingsForm key={settings.data.settings.updated_at} settings={settings.data.settings} /> : <Skeleton className="h-64 w-full" />}
    </>
  )
}

function SettingsForm({ settings }: { settings: AgencySettings }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState({
    name: settings.name,
    public_base_url: settings.public_base_url ?? '',
    default_model: settings.default_model,
    effort: settings.effort,
    input_usd_per_million: String(settings.input_usd_per_million),
    output_usd_per_million: String(settings.output_usd_per_million),
    chat_retention_days: String(settings.chat_retention_days),
    logo_url: settings.logo_url ?? '',
    primary_color: settings.primary_color ?? '',
  })
  const [texts, setTexts] = useState<Record<string, string>>(settings.texts ?? {})
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm({ ...form, [key]: e.target.value })

  const save = useMutation({
    mutationFn: () =>
      put('/panel/agency/settings', {
        ...form,
        input_usd_per_million: Number(form.input_usd_per_million.replace(',', '.')),
        output_usd_per_million: Number(form.output_usd_per_million.replace(',', '.')),
        chat_retention_days: Number.parseInt(form.chat_retention_days, 10),
        texts,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agency-settings'] })
      toast.success('Configuración guardada.')
    },
  })

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
      className="space-y-4"
    >
      <Tabs defaultValue="general">
        <TabsList className="h-auto! max-w-full flex-wrap justify-start">
          <TabsTrigger value="general">General</TabsTrigger>
          <TabsTrigger value="ai">Claude</TabsTrigger>
          <TabsTrigger value="brand">Marca del widget</TabsTrigger>
        </TabsList>
        <TabsContent value="general" className="grid max-w-2xl gap-4 pt-4">
          <div className="space-y-1.5">
            <Label htmlFor="ag-name">Nombre de la agencia</Label>
            <Input id="ag-name" value={form.name} onChange={set('name')} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ag-url">Dirección pública del panel</Label>
            <Input id="ag-url" value={form.public_base_url} onChange={set('public_base_url')} placeholder="https://bots.pixeldc.com" />
            <p className="text-xs text-muted-foreground">Con ella se arma la URL del webhook de Meta de cada bot y los enlaces de invitación.</p>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ag-retention">Días que se conservan las conversaciones inactivas</Label>
            <Input id="ag-retention" inputMode="numeric" value={form.chat_retention_days} onChange={set('chat_retention_days')} className="w-32" />
            <p className="text-xs text-muted-foreground">Los contactos y el consumo mensual por bot se conservan siempre.</p>
          </div>
        </TabsContent>
        <TabsContent value="ai" className="grid max-w-2xl gap-4 pt-4">
          <p className="text-sm text-muted-foreground">Cada chatbot usa su propia llave de Claude (en su pestaña Integraciones). Aquí se fija el modelo por defecto y el precio con el que Pixelapp estima el costo de cada respuesta.</p>
          <div className="space-y-1.5">
            <Label htmlFor="ag-model">Modelo por defecto</Label>
            <Input id="ag-model" value={form.default_model} onChange={set('default_model')} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ag-effort">Esfuerzo de razonamiento</Label>
            <NativeSelect id="ag-effort" value={form.effort} onChange={set('effort')}>
              <option value="low">Bajo (recomendado: rápido y económico)</option>
              <option value="medium">Medio</option>
              <option value="high">Alto</option>
            </NativeSelect>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="ag-in">USD por millón de tokens de entrada</Label>
              <Input id="ag-in" inputMode="decimal" value={form.input_usd_per_million} onChange={set('input_usd_per_million')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ag-out">USD por millón de tokens de salida</Label>
              <Input id="ag-out" inputMode="decimal" value={form.output_usd_per_million} onChange={set('output_usd_per_million')} />
            </div>
          </div>
        </TabsContent>
        <TabsContent value="brand" className="grid max-w-3xl gap-4 pt-4">
          <p className="text-sm text-muted-foreground">Marca por defecto del widget. Cada cliente puede cambiarla y el color de cada chatbot manda sobre todo.</p>
          <div className="space-y-1.5">
            <Label htmlFor="ag-logo">Logo (dirección https)</Label>
            <Input id="ag-logo" value={form.logo_url} onChange={set('logo_url')} placeholder="https://pixeldc.com/…/logo.png" />
          </div>
          <div className="space-y-1.5">
            <Label id="ag-color-label">Color principal</Label>
            <ColorPicker id="ag-color" label="Color del widget" value={form.primary_color} onChange={(v) => setForm({ ...form, primary_color: v })} />
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            {WIDGET_TEXTS.map((t) => (
              <div key={t.key} className="space-y-1.5">
                <Label htmlFor={`ag-text-${t.key}`}>{t.label}</Label>
                <Input id={`ag-text-${t.key}`} value={texts[t.key] ?? ''} placeholder={t.placeholder} onChange={(e) => setTexts({ ...texts, [t.key]: e.target.value })} />
              </div>
            ))}
          </div>
        </TabsContent>
      </Tabs>
      <FieldError message={save.error instanceof ApiError ? save.error.message : undefined} />
      <Button type="submit" disabled={save.isPending}>Guardar configuración</Button>
    </form>
  )
}
