import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRoundIcon, RefreshCwIcon } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { ConfirmDialog, CopyButton, FieldError, formatDate, notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { ApiError, del, get, post, put } from '@/lib/api'
import type { ClaudeKey, MetaApp } from '@/lib/types'

/** Llave de Claude y app de Meta del bot. Cada bot tiene las suyas: así se audita el consumo por bot en Anthropic y en Pixelapp. */
export function IntegrationsPanel({ botId }: { botId: string }) {
  return (
    <div className="grid gap-4 xl:grid-cols-2">
      <ClaudeKeyCard botId={botId} />
      <MetaAppCard botId={botId} />
    </div>
  )
}

function ClaudeKeyCard({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const key = useQuery({ queryKey: ['claude-key', botId], queryFn: () => get<{ key: ClaudeKey }>(`/panel/bots/${botId}/claude-key`) })
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [clearing, setClearing] = useState(false)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['claude-key', botId] })

  const save = useMutation({
    mutationFn: () => put(`/panel/bots/${botId}/claude-key`, { key: value }),
    onSuccess: () => {
      setValue('')
      setError(null)
      refresh()
      toast.success('Llave guardada. Desde ahora el bot responde con Claude.')
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar la llave.'),
  })
  const test = useMutation({
    mutationFn: () => post<{ result: 'valid' | 'rejected' | 'unavailable' }>(`/panel/bots/${botId}/claude-key/test`, { key: value || undefined }),
    onSuccess: (r) => {
      if (r.result === 'valid') toast.success('La llave funciona.')
      else if (r.result === 'rejected') toast.error('Anthropic rechazó la llave. Revisa que esté activa y bien copiada.')
      else toast.warning('No se pudo comprobar ahora (Anthropic no respondió). Intenta en unos minutos.')
    },
    onError: notifyError,
  })
  const clear = useMutation({
    mutationFn: () => del(`/panel/bots/${botId}/claude-key`),
    onSuccess: () => {
      setClearing(false)
      refresh()
    },
    onError: notifyError,
  })

  const k = key.data?.key
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <KeyRoundIcon className="size-4 text-primary" /> Llave de Claude
          {k && (k.configured ? <Badge>Configurada</Badge> : <Badge variant="outline">Sin llave</Badge>)}
        </CardTitle>
        <CardDescription>
          Una llave por chatbot, creada en la consola de Anthropic con el nombre del bot: así su consumo se ve aparte allí y aquí. Sin llave, el bot responde con el fragmento más parecido de su conocimiento, sin IA y sin costo.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {key.isLoading ? (
          <Skeleton className="h-10 w-full" />
        ) : (
          k?.configured && (
            <p className="text-sm">
              Termina en <span className="font-mono">…{k.hint}</span>
              {k.set_at && <span className="text-muted-foreground"> · guardada {formatDate(k.set_at)}</span>}
            </p>
          )
        )}
        <form
          className="space-y-2"
          onSubmit={(e) => {
            e.preventDefault()
            save.mutate()
          }}
        >
          <Label htmlFor="claude-key">{k?.configured ? 'Reemplazar llave' : 'Llave'}</Label>
          <Input id="claude-key" type="password" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} placeholder="sk-ant-…" />
          <FieldError message={error ?? undefined} />
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={save.isPending || !value.trim()}>Guardar</Button>
            <Button type="button" variant="outline" disabled={test.isPending || (!value.trim() && !k?.configured)} onClick={() => test.mutate()}>
              Probar
            </Button>
            {k?.configured && (
              <Button type="button" variant="ghost" onClick={() => setClearing(true)}>Quitar</Button>
            )}
          </div>
        </form>
      </CardContent>
      <ConfirmDialog
        open={clearing}
        onOpenChange={setClearing}
        title="Quitar la llave"
        description="El bot seguirá atendiendo, pero sin IA: responderá con el fragmento más parecido de su conocimiento."
        confirmLabel="Quitar"
        pending={clear.isPending}
        onConfirm={() => clear.mutate()}
      />
    </Card>
  )
}

function MetaAppCard({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const app = useQuery({ queryKey: ['meta-app', botId], queryFn: () => get<{ meta_app: MetaApp }>(`/panel/bots/${botId}/meta-app`) })
  const [secret, setSecret] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [regenerating, setRegenerating] = useState(false)
  const setData = (r: { meta_app: MetaApp }) => queryClient.setQueryData(['meta-app', botId], r)

  const save = useMutation({
    mutationFn: () => put<{ meta_app: MetaApp }>(`/panel/bots/${botId}/meta-app/secret`, { app_secret: secret }),
    onSuccess: (r) => {
      setSecret('')
      setError(null)
      setData(r)
      toast.success('Clave secreta guardada.')
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar la clave.'),
  })
  const clear = useMutation({
    mutationFn: () => del<{ meta_app: MetaApp }>(`/panel/bots/${botId}/meta-app/secret`),
    onSuccess: (r) => r && setData(r),
    onError: notifyError,
  })
  const regenerate = useMutation({
    mutationFn: () => post<{ meta_app: MetaApp }>(`/panel/bots/${botId}/meta-app/verify-token`),
    onSuccess: (r) => {
      setRegenerating(false)
      setData(r)
      toast.success('Token nuevo generado. Cópialo otra vez en Meta.')
    },
    onError: notifyError,
  })

  const m = app.data?.meta_app
  const absolute = m?.webhook_url.startsWith('https://')
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          App de Meta
          {m && (m.has_app_secret ? <Badge>Conectada</Badge> : <Badge variant="outline">Falta la clave secreta</Badge>)}
        </CardTitle>
        <CardDescription>
          Este bot usa su propia app de Meta para WhatsApp, Messenger e Instagram. En la configuración del webhook de esa app pega la URL y el token de abajo; después guarda aquí la clave secreta de la app.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!m ? (
          <Skeleton className="h-24 w-full" />
        ) : (
          <>
            <div className="space-y-1.5">
              <Label>URL de devolución de llamada (webhook)</Label>
              <div className="flex items-center gap-2">
                <Input readOnly value={absolute ? m.webhook_url : `${window.location.origin}${m.webhook_url}`} className="font-mono text-xs" />
                <CopyButton value={absolute ? m.webhook_url : `${window.location.origin}${m.webhook_url}`} />
              </div>
              {!absolute && <p className="text-xs text-muted-foreground">Configura la dirección pública del panel en Configuración para que esta URL sea la definitiva.</p>}
            </div>
            <div className="space-y-1.5">
              <Label>Token de verificación</Label>
              <div className="flex items-center gap-2">
                <Input readOnly value={m.verify_token} className="font-mono text-xs" />
                <CopyButton value={m.verify_token} />
                <Button type="button" size="icon" variant="ghost" aria-label="Generar otro token" onClick={() => setRegenerating(true)}>
                  <RefreshCwIcon />
                </Button>
              </div>
              <p className="text-xs text-muted-foreground">Es distinto en cada bot y se generó al crearlo.</p>
            </div>
            <form
              className="space-y-2"
              onSubmit={(e) => {
                e.preventDefault()
                save.mutate()
              }}
            >
              <Label htmlFor="meta-secret">{m.has_app_secret ? `Clave secreta de la app (termina en …${m.app_secret_hint})` : 'Clave secreta de la app'}</Label>
              <Input id="meta-secret" type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} placeholder="Configuración de la app › Básica › Clave secreta" />
              <FieldError message={error ?? undefined} />
              <div className="flex flex-wrap gap-2">
                <Button type="submit" disabled={save.isPending || !secret.trim()}>{m.has_app_secret ? 'Reemplazar' : 'Guardar'}</Button>
                {m.has_app_secret && (
                  <Button type="button" variant="ghost" disabled={clear.isPending} onClick={() => clear.mutate()}>
                    Quitar
                  </Button>
                )}
              </div>
              <p className="text-xs text-muted-foreground">Sin clave secreta, el webhook de este bot no acepta mensajes: con ella se comprueba que cada mensaje viene de su app de Meta.</p>
            </form>
          </>
        )}
      </CardContent>
      <ConfirmDialog
        open={regenerating}
        onOpenChange={setRegenerating}
        title="Generar otro token"
        description="El token actual deja de servir. Tendrás que pegar el nuevo en la configuración del webhook de la app de Meta."
        confirmLabel="Generar"
        pending={regenerate.isPending}
        onConfirm={() => regenerate.mutate()}
      />
    </Card>
  )
}
