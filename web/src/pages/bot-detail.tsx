import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeftIcon, ImagePlusIcon, Trash2Icon } from 'lucide-react'
import { useRef, useState } from 'react'
import { ColorPicker } from '@/components/color-picker'
import { BotAvatar } from '@/components/user-avatar'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { ConfirmDialog, FieldError, PageHeader, notifyError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, api, del, get, patch } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { resizeToSquare } from '@/lib/image'
import { ChannelsPanel } from '@/pages/bot-channels'
import { ConnectionsPanel } from '@/pages/bot-connections'
import { IntegrationsPanel } from '@/pages/bot-integrations'
import { ConversationsPanel, InstallPanel, LeadsPanel } from '@/pages/bot-chat'
import KnowledgePanel from '@/pages/bot-knowledge'
import { QuickRepliesPanel } from '@/pages/bot-quick-replies'
import { UsagePanel } from '@/pages/bot-usage'
import type { Bot } from '@/lib/types'

export default function BotDetailPage() {
  const { id } = useParams()
  const { isAgency } = useAuth()
  const isClientAccount = !isAgency
  const bot = useQuery({ queryKey: ['bot', id], queryFn: () => get<{ bot: Bot }>(`/panel/bots/${id}`), enabled: !!id })

  return (
    <>
      <Button variant="ghost" size="sm" render={<Link to="/chatbots" />}>
        <ArrowLeftIcon /> Chatbots
      </Button>
      {bot.isLoading || !bot.data ? (
        bot.isError ? <p className="text-sm text-muted-foreground">No se encontró este chatbot.</p> : <Skeleton className="h-64 w-full" />
      ) : (
        <>
          <PageHeader title={bot.data.bot.name} description={bot.data.bot.public_key} />
          <Tabs defaultValue={isClientAccount ? 'knowledge' : 'config'}>
            <TabsList className="h-auto! max-w-full flex-wrap justify-start">
              {!isClientAccount && <TabsTrigger value="config">Configuración</TabsTrigger>}
              {!isClientAccount && <TabsTrigger value="integrations">Integraciones</TabsTrigger>}
              <TabsTrigger value="knowledge">Conocimiento</TabsTrigger>
              {!isClientAccount && <TabsTrigger value="install">Instalación</TabsTrigger>}
              {!isClientAccount && <TabsTrigger value="channels">Canales</TabsTrigger>}
              {!isClientAccount && <TabsTrigger value="data">Datos en vivo</TabsTrigger>}
              <TabsTrigger value="conversations">Conversaciones</TabsTrigger>
              <TabsTrigger value="leads">Contactos</TabsTrigger>
              <TabsTrigger value="quick">Respuestas rápidas</TabsTrigger>
              <TabsTrigger value="usage">Consumo</TabsTrigger>
            </TabsList>
            <TabsContent value="config" className="pt-4">
              <ConfigForm key={bot.data.bot.id} bot={bot.data.bot} />
            </TabsContent>
            <TabsContent value="integrations" className="pt-4">
              <IntegrationsPanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="knowledge" className="pt-4">
              <KnowledgePanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="install" className="pt-4">
              <InstallPanel bot={bot.data.bot} />
            </TabsContent>
            <TabsContent value="channels" className="pt-4">
              <ChannelsPanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="data" className="pt-4">
              <ConnectionsPanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="conversations" className="pt-4">
              <ConversationsPanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="leads" className="pt-4">
              <LeadsPanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="quick" className="pt-4">
              <QuickRepliesPanel botId={bot.data.bot.id} />
            </TabsContent>
            <TabsContent value="usage" className="pt-4">
              <UsagePanel botId={bot.data.bot.id} />
            </TabsContent>
          </Tabs>
        </>
      )}
    </>
  )
}

/** Configuración del bot. Los campos vacíos borran el valor; los sitios permitidos van uno por línea. */
function ConfigForm({ bot }: { bot: Bot }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [name, setName] = useState(bot.name)
  const [welcome, setWelcome] = useState(bot.welcome_message ?? '')
  const [tone, setTone] = useState(bot.tone ?? '')
  const [farewell, setFarewell] = useState(bot.farewell ?? '')
  const [handoff, setHandoff] = useState(bot.handoff_message ?? '')
  const [returnHours, setReturnHours] = useState(String(bot.handoff_return_hours ?? 2))
  const [cta, setCta] = useState(bot.call_to_action ?? '')
  const [address, setAddress] = useState(bot.address ?? '')
  const [coordinates, setCoordinates] = useState(bot.coordinates ?? '')
  const [whatsapp, setWhatsapp] = useState(bot.whatsapp_number ?? '')
  const [prompt, setPrompt] = useState(bot.system_prompt ?? '')
  const [color, setColor] = useState(bot.color ?? '')
  const [privacy, setPrivacy] = useState(bot.privacy_url ?? '')
  const [origins, setOrigins] = useState(bot.allowed_origins.join('\n'))
  const [active, setActive] = useState(bot.is_active)
  const [error, setError] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)

  const save = useMutation({
    mutationFn: () =>
      patch<{ bot: Bot }>(`/panel/bots/${bot.id}`, {
        name,
        welcome_message: welcome,
        farewell,
        handoff_message: handoff,
        handoff_return_hours: Math.min(24, Math.max(0, Number.parseInt(returnHours, 10) || 0)),
        call_to_action: cta,
        address,
        coordinates,
        whatsapp_number: whatsapp,
        tone,
        system_prompt: prompt,
        color,
        privacy_url: privacy,
        allowed_origins: origins.split('\n').map((o) => o.trim()).filter(Boolean),
        is_active: active,
      }),
    onSuccess: (r) => {
      setError(null)
      queryClient.setQueryData(['bot', bot.id], r)
      queryClient.invalidateQueries({ queryKey: ['bots'] })
      toast.success('Cambios guardados.')
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudieron guardar los cambios.'),
  })

  const fileInput = useRef<HTMLInputElement>(null)
  const refreshBot = () => {
    queryClient.invalidateQueries({ queryKey: ['bot', bot.id] })
    queryClient.invalidateQueries({ queryKey: ['bots'] })
  }
  const uploadIcon = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData()
      form.append('file', await resizeToSquare(file))
      return api('PUT', `/panel/bots/${bot.id}/icon`, form)
    },
    onSuccess: () => {
      refreshBot()
      toast.success('Icono actualizado.')
    },
    onError: notifyError,
  })
  const removeIcon = useMutation({
    mutationFn: () => del(`/panel/bots/${bot.id}/icon`),
    onSuccess: () => {
      refreshBot()
      toast.success('Icono quitado.')
    },
    onError: notifyError,
  })

  const remove = useMutation({
    mutationFn: () => del(`/panel/bots/${bot.id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['bots'] })
      navigate('/chatbots', { replace: true })
    },
    onError: notifyError,
  })

  return (
    <>
      <form
        className="grid w-full gap-4 lg:grid-cols-2"
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate()
        }}
      >
        <div className="space-y-1.5 lg:col-span-2">
          <Label>Icono del chatbot</Label>
          <div className="flex flex-wrap items-center gap-4 rounded-xl border bg-card p-3">
            <BotAvatar bot={{ ...bot, color }} size="lg" className="size-16!" />
            <div className="space-y-2">
              <div className="flex flex-wrap gap-2">
                <input
                  ref={fileInput}
                  type="file"
                  accept="image/png,image/jpeg,image/webp"
                  className="sr-only"
                  aria-label="Elegir icono del chatbot"
                  onChange={(e) => {
                    const file = e.target.files?.[0]
                    e.target.value = ''
                    if (file) uploadIcon.mutate(file)
                  }}
                />
                <Button type="button" variant="outline" size="sm" disabled={uploadIcon.isPending} onClick={() => fileInput.current?.click()}>
                  <ImagePlusIcon /> {bot.icon_version ? 'Cambiar icono' : 'Subir icono'}
                </Button>
                {bot.icon_version ? (
                  <Button type="button" variant="ghost" size="sm" disabled={removeIcon.isPending} onClick={() => removeIcon.mutate()}>
                    <Trash2Icon /> Quitar
                  </Button>
                ) : null}
              </div>
              <p className="text-xs text-muted-foreground">Se ve en la cabecera del chat de tu web y en este panel. PNG, JPG o WebP; se recorta en cuadrado y se reduce sola. Se guarda al elegirlo.</p>
            </div>
          </div>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-name">Nombre</Label>
          <Input id="bot-name" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-welcome">Mensaje de bienvenida</Label>
          <Input id="bot-welcome" value={welcome} onChange={(e) => setWelcome(e.target.value)} placeholder="Saludo con el que el asistente abre la conversación" />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-farewell">Mensaje de despedida</Label>
          <Input id="bot-farewell" value={farewell} onChange={(e) => setFarewell(e.target.value)} placeholder="Frase con la que se despide al terminar la conversación" />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-handoff">Mensaje al pasar a un operador</Label>
          <Input id="bot-handoff" value={handoff} onChange={(e) => setHandoff(e.target.value)} placeholder="Aviso al contacto de que una persona del equipo lo atenderá" />
          <p className="text-xs text-muted-foreground">
            En WhatsApp, Messenger e Instagram, si el contacto pide hablar con una persona o se molesta, el asistente dice este mensaje y deja de responder. La conversación queda marcada en Conversaciones para que la tomes; con «Devolver al bot» vuelve a atender.
          </p>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-return">El bot retoma la conversación tras (horas)</Label>
          <Input id="bot-return" type="number" min={0} max={24} inputMode="numeric" value={returnHours} onChange={(e) => setReturnHours(e.target.value)} className="w-28" />
          <p className="text-xs text-muted-foreground">
            Si nadie del equipo interviene en ese tiempo y el contacto vuelve a escribir, el bot responde de nuevo. Con 0 solo atiende otra vez cuando alguien pulsa «Devolver al bot».
          </p>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-cta">Llamado a la acción</Label>
          <Input id="bot-cta" value={cta} onChange={(e) => setCta(e.target.value)} placeholder="Invitación que propone al cerrar (visitar, comprar, reservar, escribir…)" />
          <p className="text-xs text-muted-foreground">El asistente lo propone al cerrar una respuesta cuando es natural, sin repetirlo en cada mensaje.</p>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bot-address">Dirección</Label>
          <Input id="bot-address" value={address} onChange={(e) => setAddress(e.target.value)} placeholder="Dirección completa del negocio, con ciudad" />
        </div>
        <div className="grid gap-4 sm:grid-cols-2 lg:col-span-2">
          <div className="space-y-1.5">
            <Label htmlFor="bot-coords">Coordenadas (opcional)</Label>
            <Input id="bot-coords" value={coordinates} onChange={(e) => setCoordinates(e.target.value)} placeholder="Latitud, longitud" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="bot-wa">WhatsApp del negocio</Label>
            <Input id="bot-wa" value={whatsapp} onChange={(e) => setWhatsapp(e.target.value)} placeholder="Número con indicativo de país, solo dígitos" inputMode="numeric" />
          </div>
        </div>
        <p className="-mt-2 text-xs text-muted-foreground lg:col-span-2">
          Con la dirección (y las coordenadas, si las tienes) el asistente comparte siempre el enlace de Google Maps, con mapa en miniatura en el chat web. Con el WhatsApp comparte el enlace para abrir la conversación. No hace falta cargarlos como conocimiento.
        </p>
        <div className="space-y-1.5">
          <Label htmlFor="bot-tone">Tono</Label>
          <Input id="bot-tone" value={tone} onChange={(e) => setTone(e.target.value)} placeholder="Cómo debe sonar: cercano, formal, divertido…" />
        </div>
        <div className="space-y-1.5 lg:col-span-2">
          <Label htmlFor="bot-prompt">Instrucciones del negocio</Label>
          <Textarea id="bot-prompt" value={prompt} onChange={(e) => setPrompt(e.target.value)} rows={5} placeholder="A qué se dedica el negocio, a quién atiende, cómo debe hablar el asistente y qué no debe prometer." />
          <p className="text-xs text-muted-foreground">El asistente solo responde con la información que cargues y estas instrucciones.</p>
        </div>
        <div className="grid gap-4 sm:grid-cols-2 lg:col-span-2">
          <div className="space-y-1.5">
            <Label id="bot-color-label">Color</Label>
            <ColorPicker id="bot-color" label="Color del chat" value={color} onChange={setColor} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="bot-privacy">Política de privacidad</Label>
            <Input id="bot-privacy" value={privacy} onChange={(e) => setPrivacy(e.target.value)} placeholder="Enlace a la política de privacidad del negocio" />
          </div>
        </div>
        <div className="space-y-1.5 lg:col-span-2">
          <Label htmlFor="bot-origins">Sitios donde puede aparecer</Label>
          <Textarea id="bot-origins" value={origins} onChange={(e) => setOrigins(e.target.value)} rows={3} placeholder={'https://minegocio.com\nhttps://www.minegocio.com'} />
          <p className="text-xs text-muted-foreground">Uno por línea. Si lo dejas vacío, puede incrustarse en cualquier sitio.</p>
        </div>
        <label className="flex items-center gap-2 text-sm lg:col-span-2">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
          Chatbot activo
        </label>
        <div className="lg:col-span-2">
          <FieldError message={error ?? undefined} />
        </div>
        <div className="flex gap-2 lg:col-span-2">
          <Button type="submit" disabled={save.isPending}>
            Guardar cambios
          </Button>
          <Button type="button" variant="outline" onClick={() => setDeleting(true)}>
            Eliminar chatbot
          </Button>
        </div>
      </form>

      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title="Eliminar chatbot"
        description={`Se borrarán "${bot.name}", su información, sus conversaciones y los datos de contacto capturados. No se puede deshacer.`}
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </>
  )
}
