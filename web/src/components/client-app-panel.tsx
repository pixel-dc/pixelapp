import { useMutation, useQueryClient } from '@tanstack/react-query'
import { CopyIcon, ImageIcon, Trash2Icon } from 'lucide-react'
import { useRef, useState } from 'react'
import { toast } from 'sonner'
import { notifyError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { api, del } from '@/lib/api'
import { clientAppUrl, useBranding } from '@/lib/branding'
import { appIconPng } from '@/lib/image'
import type { Client } from '@/lib/types'

/**
 * La app del cliente (solo la agencia): el enlace de su acceso propio, que se instala en el teléfono con su nombre y su icono, y donde solo
 * entran sus usuarios y reciben solo sus notificaciones.
 */
export function ClientAppPanel({ client }: { client: Client }) {
  const branding = useBranding()
  const queryClient = useQueryClient()
  const url = clientAppUrl(branding, client.slug)
  const fileInput = useRef<HTMLInputElement>(null)
  const [background, setBackground] = useState('#ffffff')
  const version = client.app_icon_version ?? null

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['client', client.id] })
  const upload = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData()
      form.append('file', await appIconPng(file, background))
      return api('PUT', `/panel/clients/${client.id}/app-icon`, form)
    },
    onSuccess: () => {
      refresh()
      toast.success('Icono de la app actualizado.')
    },
    onError: notifyError,
  })
  const remove = useMutation({
    mutationFn: () => del(`/panel/clients/${client.id}/app-icon`),
    onSuccess: () => {
      refresh()
      toast.success('Icono quitado: su app usa el de la agencia.')
    },
    onError: notifyError,
  })

  const copy = async () => {
    if (!url) return
    try {
      await navigator.clipboard.writeText(url)
      toast.success('Enlace copiado.')
    } catch {
      toast.error('No se pudo copiar. Selecciónalo y cópialo a mano.')
    }
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>Enlace de su app</CardTitle>
          <CardDescription>
            Compártelo con {client.name}. Desde ahí entra su equipo, instala la app en el teléfono con su nombre y su icono, y recibe solo las
            notificaciones de sus chatbots.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          {url ? (
            <div className="flex gap-2">
              <Input readOnly value={url} aria-label="Enlace de la app del cliente" onFocus={(e) => e.currentTarget.select()} />
              <Button variant="outline" onClick={copy}>
                <CopyIcon /> Copiar
              </Button>
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">Falta configurar el dominio de los accesos por cliente en el servidor (ClientApps:Domain).</p>
          )}
          <ul className="list-disc space-y-1 pl-5 text-sm text-muted-foreground">
            <li>
              <span className="font-medium text-foreground">iPhone:</span> abrir el enlace en Safari → Compartir → «Añadir a pantalla de inicio». Las
              notificaciones se activan desde la app ya instalada (iOS 16.4 o superior).
            </li>
            <li>
              <span className="font-medium text-foreground">Android:</span> abrir el enlace en Chrome → menú ⋮ → «Instalar app» o «Añadir a pantalla de
              inicio».
            </li>
            <li>Después de entrar, en la bandeja o en «Mi cuenta» se toca «Activar notificaciones».</li>
          </ul>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Icono de la app</CardTitle>
          <CardDescription>
            Lo que se ve en la pantalla de inicio del teléfono y en las notificaciones. Sube el logo del cliente: se centra sobre el fondo elegido y se
            guarda en PNG cuadrado. Si ya instalaron la app, el icono nuevo se ve al volver a instalarla.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap items-center gap-4">
          <span className="grid size-24 shrink-0 place-items-center overflow-hidden rounded-2xl border bg-muted shadow-sm">
            {version ? (
              <img src={`/panel/clients/${client.id}/app-icon?v=${version}`} alt={`Icono de la app de ${client.name}`} className="size-full object-cover" />
            ) : (
              <ImageIcon className="size-8 text-muted-foreground" aria-label="Sin icono propio" />
            )}
          </span>
          <div className="space-y-3">
            <div className="flex items-center gap-2">
              <Label htmlFor="app-icon-bg" className="text-sm">
                Fondo
              </Label>
              <input
                id="app-icon-bg"
                type="color"
                value={background}
                onChange={(e) => setBackground(e.target.value)}
                className="h-8 w-12 cursor-pointer rounded border bg-transparent"
              />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => fileInput.current?.click()} disabled={upload.isPending}>
                {version ? 'Cambiar icono' : 'Subir icono'}
              </Button>
              {version && (
                <Button variant="ghost" onClick={() => remove.mutate()} disabled={remove.isPending}>
                  <Trash2Icon /> Quitar
                </Button>
              )}
            </div>
            {!version && <p className="text-xs text-muted-foreground">Sin icono propio su app usa el de la agencia.</p>}
          </div>
          <input
            ref={fileInput}
            type="file"
            accept="image/png,image/jpeg,image/webp"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0]
              e.target.value = ''
              if (file) upload.mutate(file)
            }}
          />
        </CardContent>
      </Card>
    </div>
  )
}
