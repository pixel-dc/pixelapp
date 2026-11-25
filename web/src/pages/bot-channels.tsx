import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CircleCheckIcon, CircleXIcon, PlusIcon, ShieldCheckIcon, TriangleAlertIcon, WrenchIcon } from 'lucide-react'
import { useState } from 'react'
import { ConfirmDialog, EmptyState, FieldError, NativeSelect, formatDate, notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ApiError, del, get, patch, post } from '@/lib/api'
import type { AppliedFix, Channel, ConnectionCheck, ConnectionReport } from '@/lib/types'

interface ChannelList {
  channels: Channel[]
}

export const KIND_LABEL: Record<Channel['kind'], string> = { whatsapp: 'WhatsApp', messenger: 'Messenger', instagram: 'Instagram' }

const ID_LABEL: Record<Channel['kind'], string> = {
  whatsapp: 'ID del número de teléfono',
  messenger: 'ID de la página de Facebook',
  instagram: 'ID de la cuenta de Instagram',
}

const STATUS: Record<Channel['status'], { label: string; variant: 'secondary' | 'outline' | 'destructive' }> = {
  active: { label: 'Activo', variant: 'secondary' },
  disabled: { label: 'Desactivado', variant: 'outline' },
  tokenerror: { label: 'Token rechazado', variant: 'destructive' },
}

/** Canales de Meta (WhatsApp, Messenger e Instagram) conectados a este chatbot. */
export function ChannelsPanel({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const key = ['bot-channels', botId]
  const list = useQuery({ queryKey: key, queryFn: () => get<ChannelList>(`/panel/bots/${botId}/channels`) })
  const [adding, setAdding] = useState(false)
  const [tokenFor, setTokenFor] = useState<Channel | null>(null)
  const [accountFor, setAccountFor] = useState<Channel | null>(null)
  const [removing, setRemoving] = useState<Channel | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: key })

  const toggle = useMutation({
    mutationFn: (c: Channel) => patch(`/panel/bots/${botId}/channels/${c.id}`, { status: c.status === 'disabled' ? 'active' : 'disabled' }),
    onSuccess: refresh,
    onError: notifyError,
  })
  const remove = useMutation({
    mutationFn: (id: string) => del(`/panel/bots/${botId}/channels/${id}`),
    onSuccess: () => {
      setRemoving(null)
      refresh()
    },
    onError: notifyError,
  })

  const data = list.data

  if (list.isLoading || !data) return <Skeleton className="h-40 w-full" />

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="max-w-xl text-sm text-muted-foreground">
          El chatbot responde a los mensajes que le escriban por estos canales, con la misma información que usa en la web. Los canales usan la app de Meta del bot (pestaña Integraciones).
        </p>
        <Button onClick={() => setAdding(true)}>
          <PlusIcon /> Conectar canal
        </Button>
      </div>

      {!data.channels.length ? (
        <EmptyState>Todavía no hay canales conectados.</EmptyState>
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Canal</TableHead>
                <TableHead className="hidden md:table-cell">Identificador</TableHead>
                <TableHead>Estado</TableHead>
                <TableHead className="hidden lg:table-cell">Conectado</TableHead>
                <TableHead className="w-72" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.channels.map((c) => (
                <TableRow key={c.id} className={c.status === 'active' ? '' : 'opacity-70'}>
                  <TableCell>
                    <div className="font-medium">{c.display_name}</div>
                    <div className="text-xs text-muted-foreground">{KIND_LABEL[c.kind]}</div>
                  </TableCell>
                  <TableCell className="hidden font-mono text-xs md:table-cell">
                    {c.external_id}
                    {c.kind === 'whatsapp' && <div className="text-muted-foreground">WABA: {c.account_id ?? 'sin indicar'}</div>}
                  </TableCell>
                  <TableCell>
                    <Badge variant={STATUS[c.status].variant}>{STATUS[c.status].label}</Badge>
                  </TableCell>
                  <TableCell className="hidden lg:table-cell">{formatDate(c.created_at)}</TableCell>
                  <TableCell className="text-right">
                    {c.kind === 'whatsapp' && (
                      <Button variant="ghost" size="sm" onClick={() => setAccountFor(c)} aria-label={`Cambiar el WABA de ${c.display_name}`}>
                        WABA
                      </Button>
                    )}
                    <Button variant="ghost" size="sm" onClick={() => setTokenFor(c)} aria-label={`Cambiar token de ${c.display_name}`}>
                      Cambiar token
                    </Button>
                    <Button variant="ghost" size="sm" disabled={toggle.isPending || c.status === 'tokenerror'} onClick={() => toggle.mutate(c)} aria-label={`${c.status === 'disabled' ? 'Activar' : 'Desactivar'} ${c.display_name}`}>
                      {c.status === 'disabled' ? 'Activar' : 'Desactivar'}
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => setRemoving(c)} aria-label={`Quitar ${c.display_name}`}>
                      Quitar
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      {data.channels.some((c) => c.status === 'tokenerror') && (
        <p className="text-sm text-destructive">Meta rechazó el token de algún canal (venció o se revocó). Pega uno nuevo con «Cambiar token» y vuelve a funcionar.</p>
      )}

      {data.channels.length > 0 && <ConnectionCard botId={botId} onFixed={refresh} />}

      <section className="w-full space-y-2 text-sm">
        <h3 className="font-medium">Configuración en Meta</h3>
        <p className="text-muted-foreground">
          La URL del webhook, el token de verificación y la clave secreta de la app de Meta de este bot están en la pestaña Integraciones. «Verificar conexión» revisa todo contra Meta y «Reparar» corrige lo que se pueda sin entrar a Meta.
        </p>
        <p className="text-xs text-muted-foreground">
          El chatbot solo responde a quien le escribe, dentro de las 24 horas que Meta permite. Cada conversación de un contacto dura 24 horas y cuenta como una.
        </p>
      </section>

      <AddDialog botId={botId} open={adding} onOpenChange={setAdding} onCreated={refresh} />
      <TokenDialog botId={botId} channel={tokenFor} onClose={() => setTokenFor(null)} onSaved={refresh} />
      <AccountDialog key={accountFor?.id ?? 'none'} botId={botId} channel={accountFor} onClose={() => setAccountFor(null)} onSaved={refresh} />
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Quitar canal"
        description={`El chatbot dejará de responder por ${removing?.display_name ?? 'este canal'}. Las conversaciones ya guardadas se conservan.`}
        confirmLabel="Quitar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing.id)}
      />
    </div>
  )
}

function AddDialog({ botId, open, onOpenChange, onCreated }: { botId: string; open: boolean; onOpenChange: (o: boolean) => void; onCreated: () => void }) {
  const [kind, setKind] = useState<Channel['kind']>('whatsapp')
  const [externalId, setExternalId] = useState('')
  const [accountId, setAccountId] = useState('')
  const [name, setName] = useState('')
  const [token, setToken] = useState('')
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: () =>
      post(`/panel/bots/${botId}/channels`, {
        kind,
        external_id: externalId,
        display_name: name,
        access_token: token,
        ...(kind === 'whatsapp' && accountId.trim() ? { account_id: accountId.trim() } : {}),
      }),
    onSuccess: () => {
      setExternalId('')
      setAccountId('')
      setName('')
      setToken('')
      setError(null)
      onCreated()
      onOpenChange(false)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo conectar el canal.'),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Conectar canal</DialogTitle>
          <DialogDescription>Los datos salen de la app de Meta del bot. El token se guarda cifrado y no se vuelve a mostrar.</DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            create.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="ch-kind">Canal</Label>
            <NativeSelect id="ch-kind" value={kind} onChange={(e) => setKind(e.target.value as Channel['kind'])}>
              {(Object.keys(KIND_LABEL) as Channel['kind'][]).map((k) => (
                <option key={k} value={k}>{KIND_LABEL[k]}</option>
              ))}
            </NativeSelect>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ch-id">{ID_LABEL[kind]}</Label>
            <Input id="ch-id" value={externalId} onChange={(e) => setExternalId(e.target.value)} autoComplete="off" />
          </div>
          {kind === 'whatsapp' && (
            <div className="space-y-1.5">
              <Label htmlFor="ch-waba">ID de la cuenta de WhatsApp Business (WABA)</Label>
              <Input id="ch-waba" value={accountId} onChange={(e) => setAccountId(e.target.value)} autoComplete="off" />
              <p className="text-xs text-muted-foreground">Opcional. Con él, «Verificar conexión» comprueba y corrige que la app esté suscrita a la cuenta.</p>
            </div>
          )}
          <div className="space-y-1.5">
            <Label htmlFor="ch-name">Nombre para reconocerlo</Label>
            <Input id="ch-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Nombre para reconocer este canal" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="ch-token">Token de acceso de Meta</Label>
            <Input id="ch-token" type="password" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" />
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="submit" disabled={create.isPending}>
              Conectar
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function TokenDialog({ botId, channel, onClose, onSaved }: { botId: string; channel: Channel | null; onClose: () => void; onSaved: () => void }) {
  const [token, setToken] = useState('')
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () => patch(`/panel/bots/${botId}/channels/${channel!.id}`, { access_token: token }),
    onSuccess: () => {
      setToken('')
      setError(null)
      onSaved()
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar el token.'),
  })

  return (
    <Dialog open={!!channel} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Cambiar token de {channel?.display_name}</DialogTitle>
          <DialogDescription>El token anterior se reemplaza y el canal vuelve a funcionar.</DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            save.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="tk-token">Token de acceso nuevo</Label>
            <Input id="tk-token" type="password" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" autoFocus />
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="submit" disabled={save.isPending || !token.trim()}>
              Guardar
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function AccountDialog({ botId, channel, onClose, onSaved }: { botId: string; channel: Channel | null; onClose: () => void; onSaved: () => void }) {
  const [value, setValue] = useState(channel?.account_id ?? '')
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () => patch(`/panel/bots/${botId}/channels/${channel!.id}`, { account_id: value.trim() }),
    onSuccess: () => {
      setError(null)
      onSaved()
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar.'),
  })

  return (
    <Dialog open={!!channel} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Cuenta de WhatsApp de {channel?.display_name}</DialogTitle>
          <DialogDescription>
            El ID de la cuenta de WhatsApp Business (WABA) está en Meta, en WhatsApp → Configuración de la API. Pixela lo usa para comprobar que la app esté suscrita a la cuenta. Déjalo vacío para quitarlo.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            save.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="ac-id">ID del WABA</Label>
            <Input id="ac-id" value={value} onChange={(e) => setValue(e.target.value)} autoComplete="off" autoFocus />
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="submit" disabled={save.isPending}>
              Guardar
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

const CHECK_ICON: Record<ConnectionCheck['status'], { Icon: typeof CircleCheckIcon; className: string; label: string }> = {
  ok: { Icon: CircleCheckIcon, className: 'text-emerald-600', label: 'Correcto' },
  warning: { Icon: TriangleAlertIcon, className: 'text-amber-600', label: 'Atención' },
  error: { Icon: CircleXIcon, className: 'text-destructive', label: 'Error' },
}

/** Revisa la conexión del bot contra Meta y corrige lo que se puede sin entrar a Meta for Developers. */
function ConnectionCard({ botId, onFixed }: { botId: string; onFixed: () => void }) {
  const [report, setReport] = useState<ConnectionReport | null>(null)
  const [applied, setApplied] = useState<AppliedFix[]>([])

  const check = useMutation({
    mutationFn: () => get<{ connection: ConnectionReport }>(`/panel/bots/${botId}/meta/connection`),
    onSuccess: (r) => {
      setApplied([])
      setReport(r.connection)
    },
    onError: notifyError,
  })
  const repair = useMutation({
    mutationFn: () => post<{ applied: AppliedFix[]; connection: ConnectionReport }>(`/panel/bots/${botId}/meta/connection/repair`),
    onSuccess: (r) => {
      setApplied(r.applied)
      setReport(r.connection)
      onFixed() // la reparación puede haber cambiado el token de un canal
    },
    onError: notifyError,
  })

  const busy = check.isPending || repair.isPending

  return (
    <section className="space-y-3 rounded-lg border p-4" aria-labelledby="meta-conn-title">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 id="meta-conn-title" className="flex items-center gap-2 font-medium">
            <ShieldCheckIcon className="size-4" /> Conexión con Meta
          </h3>
          <p className="text-sm text-muted-foreground">Revisa el token, el webhook y las suscripciones de este bot directamente en Meta.</p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" disabled={busy} onClick={() => check.mutate()}>
            {check.isPending ? 'Verificando…' : report ? 'Verificar de nuevo' : 'Verificar conexión'}
          </Button>
          {report?.can_repair && (
            <Button disabled={busy} onClick={() => repair.mutate()}>
              <WrenchIcon /> {repair.isPending ? 'Reparando…' : 'Reparar'}
            </Button>
          )}
        </div>
      </div>

      {applied.length > 0 && (
        <ul className="space-y-1 text-sm" aria-label="Cambios aplicados en Meta">
          {applied.map((a) => (
            <li key={a.title} className={a.success ? 'text-emerald-700 dark:text-emerald-400' : 'text-destructive'}>
              {a.success ? '✓' : '✗'} {a.title}
              {!a.success && a.message ? `: ${a.message}` : ''}
            </li>
          ))}
        </ul>
      )}

      {report && (
        <>
          <p className={`text-sm font-medium ${report.healthy ? 'text-emerald-700 dark:text-emerald-400' : ''}`} role="status">
            {report.healthy ? 'Todo está en orden: Meta entregará los mensajes a este bot.' : 'Hay puntos por revisar.'}
          </p>
          <ul className="divide-y rounded-md border text-sm">
            {report.checks.map((c, i) => {
              const { Icon, className, label } = CHECK_ICON[c.status]
              return (
                <li key={`${c.id}-${c.channel_id ?? ''}-${i}`} className="flex gap-3 p-3">
                  <Icon className={`mt-0.5 size-4 shrink-0 ${className}`} aria-label={label} />
                  <div className="min-w-0 space-y-0.5">
                    <div className="font-medium">
                      {c.title}
                      {c.fixable && c.status !== 'ok' && <Badge variant="secondary" className="ml-2 align-middle">Se puede reparar</Badge>}
                    </div>
                    {c.detail && <div className="text-muted-foreground">{c.detail}</div>}
                    {c.status !== 'ok' && !c.fixable && c.hint && <div className="text-xs">Qué hacer: {c.hint}</div>}
                  </div>
                </li>
              )
            })}
          </ul>
        </>
      )}
    </section>
  )
}
