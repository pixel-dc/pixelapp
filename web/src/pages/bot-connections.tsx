import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PlusIcon } from 'lucide-react'
import { useEffect, useState } from 'react'
import { ConfirmDialog, EmptyState, FieldError, notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { ApiError, del, get, patch, post } from '@/lib/api'

interface Connection {
  id: string
  name: string
  url: string
  is_active: boolean
  has_token: boolean
  allowed_tools: string[]
  last_error: string | null
  created_at: string
}

interface ConnectionTool {
  name: string
  description: string | null
  allowed: boolean
}

/** Nombres que sugieren que la herramienta cambia datos (crear, borrar, pagar…): se marcan para que se permitan con cuidado. */
const WRITES = /(^|[_\-.])(create|add|update|delete|remove|cancel|pay|refund|send|set|write|insert|crear|agregar|actualizar|eliminar|borrar|cancelar|pagar|reembolsar|enviar)([_\-.]|$)/i

export const looksLikeWrite = (name: string) => WRITES.test(name)

const hostOf = (url: string) => {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

/** Datos en vivo: servidores MCP (WooCommerce, Supabase…) que el chatbot consulta mientras conversa, con las herramientas que tú le permitas. */
export function ConnectionsPanel({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const key = ['bot-connections', botId]
  const list = useQuery({ queryKey: key, queryFn: () => get<{ connections: Connection[] }>(`/panel/bots/${botId}/connections`) })
  const [adding, setAdding] = useState(false)
  const [choosing, setChoosing] = useState<Connection | null>(null)
  const [tokenFor, setTokenFor] = useState<Connection | null>(null)
  const [removing, setRemoving] = useState<Connection | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: key })

  const toggle = useMutation({
    mutationFn: (c: Connection) => patch(`/panel/bots/${botId}/connections/${c.id}`, { is_active: !c.is_active }),
    onSuccess: refresh,
    onError: notifyError,
  })
  const remove = useMutation({
    mutationFn: (id: string) => del(`/panel/bots/${botId}/connections/${id}`),
    onSuccess: () => {
      setRemoving(null)
      refresh()
    },
    onError: notifyError,
  })

  if (list.isLoading || !list.data) return <Skeleton className="h-40 w-full" />
  const rows = list.data.connections

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="max-w-xl text-sm text-muted-foreground">
          Conecta un servidor MCP (por ejemplo, el de tu tienda WooCommerce) y el chatbot consulta precios, existencias o pedidos al momento de responder, en vez de depender de datos copiados. Tú eliges qué herramientas puede usar.
        </p>
        <Button onClick={() => setAdding(true)} disabled={rows.length >= 5}>
          <PlusIcon /> Conectar servidor
        </Button>
      </div>

      {!rows.length ? (
        <EmptyState>Todavía no hay datos en vivo conectados.</EmptyState>
      ) : (
        <ul className="space-y-3" aria-label="Conexiones de datos">
          {rows.map((c) => (
            <li key={c.id} className={`rounded-xl border bg-card p-4 shadow-sm ${c.is_active ? '' : 'opacity-70'}`}>
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <h3 className="font-medium">{c.name}</h3>
                    <Badge variant={c.is_active ? 'secondary' : 'outline'}>{c.is_active ? 'Activa' : 'Apagada'}</Badge>
                    {!c.allowed_tools.length && <Badge variant="outline">Sin herramientas permitidas</Badge>}
                  </div>
                  <p className="mt-0.5 truncate font-mono text-xs text-muted-foreground">{hostOf(c.url)}</p>
                  <p className="mt-1 text-sm text-muted-foreground">
                    {c.allowed_tools.length
                      ? `Puede usar ${c.allowed_tools.length} ${c.allowed_tools.length === 1 ? 'herramienta' : 'herramientas'}: ${c.allowed_tools.join(', ')}`
                      : 'El chatbot no consulta nada hasta que elijas las herramientas.'}
                  </p>
                  {c.last_error && <p className="mt-1 text-sm text-destructive">Último error: {c.last_error}</p>}
                </div>
                <div className="flex flex-wrap gap-1">
                  <Button variant="outline" size="sm" onClick={() => setChoosing(c)} aria-label={`Elegir herramientas de ${c.name}`}>
                    Herramientas
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => setTokenFor(c)} aria-label={`Cambiar token de ${c.name}`}>
                    Cambiar token
                  </Button>
                  <Button variant="ghost" size="sm" disabled={toggle.isPending} onClick={() => toggle.mutate(c)} aria-label={`${c.is_active ? 'Apagar' : 'Activar'} ${c.name}`}>
                    {c.is_active ? 'Apagar' : 'Activar'}
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => setRemoving(c)} aria-label={`Quitar ${c.name}`}>
                    Quitar
                  </Button>
                </div>
              </div>
            </li>
          ))}
        </ul>
      )}

      <section className="space-y-1 text-xs text-muted-foreground">
        <p>Lo que devuelve el servidor se trata como información, nunca como instrucciones. Cada consulta suma al consumo del chatbot (verás los tokens en Consumo).</p>
        <p>Empieza permitiendo solo herramientas de lectura. Las que crean, cambian o borran datos se marcan con una advertencia.</p>
      </section>

      <AddDialog botId={botId} open={adding} onOpenChange={setAdding} onCreated={(c) => { refresh(); setChoosing(c) }} />
      <ToolsDialog botId={botId} connection={choosing} onClose={() => setChoosing(null)} onSaved={refresh} />
      <TokenDialog botId={botId} connection={tokenFor} onClose={() => setTokenFor(null)} onSaved={refresh} />
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Quitar conexión"
        description={`El chatbot dejará de consultar «${removing?.name ?? 'este servidor'}». Tus datos en ese servidor no se tocan.`}
        confirmLabel="Quitar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing.id)}
      />
    </div>
  )
}

function AddDialog({ botId, open, onOpenChange, onCreated }: { botId: string; open: boolean; onOpenChange: (o: boolean) => void; onCreated: (c: Connection) => void }) {
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [token, setToken] = useState('')
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: () => post<{ connection: Connection }>(`/panel/bots/${botId}/connections`, { name, url, ...(token.trim() ? { token } : {}) }),
    onSuccess: (r) => {
      setName('')
      setUrl('')
      setToken('')
      setError(null)
      onOpenChange(false)
      onCreated(r.connection)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo conectar el servidor.'),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Conectar servidor MCP</DialogTitle>
          <DialogDescription>Probamos la conexión antes de guardarla. El token se guarda cifrado y no se vuelve a mostrar.</DialogDescription>
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
            <Label htmlFor="mcp-name">Nombre para reconocerlo</Label>
            <Input id="mcp-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Nombre para reconocer esta conexión" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="mcp-url">Dirección del servidor MCP</Label>
            <Input id="mcp-url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="Dirección del servidor MCP" inputMode="url" autoComplete="off" />
            <p className="text-xs text-muted-foreground">Debe empezar por https y ser pública.</p>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="mcp-token">Token de acceso (si el servidor lo pide)</Label>
            <Input id="mcp-token" type="password" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" />
            <p className="text-xs text-muted-foreground">Crea uno de solo lectura en tu tienda o servicio.</p>
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="submit" disabled={create.isPending}>
              {create.isPending ? 'Probando…' : 'Conectar'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function ToolsDialog({ botId, connection, onClose, onSaved }: { botId: string; connection: Connection | null; onClose: () => void; onSaved: () => void }) {
  const tools = useQuery({
    queryKey: ['bot-connection-tools', botId, connection?.id],
    queryFn: () => get<{ tools: ConnectionTool[] }>(`/panel/bots/${botId}/connections/${connection!.id}/tools`),
    enabled: !!connection,
    retry: false,
  })
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (tools.data) setSelected(new Set(tools.data.tools.filter((t) => t.allowed).map((t) => t.name)))
  }, [tools.data])

  const save = useMutation({
    mutationFn: () => patch(`/panel/bots/${botId}/connections/${connection!.id}`, { allowed_tools: [...selected] }),
    onSuccess: () => {
      setError(null)
      onSaved()
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudieron guardar las herramientas.'),
  })

  const toggle = (name: string, on: boolean) =>
    setSelected((prev) => {
      const next = new Set(prev)
      if (on) next.add(name)
      else next.delete(name)
      return next
    })

  const all = tools.data?.tools ?? []
  return (
    <Dialog open={!!connection} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[85dvh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Herramientas de {connection?.name}</DialogTitle>
          <DialogDescription>Marca lo que el chatbot puede consultar. Lo que no marques, no lo ve ni lo usa.</DialogDescription>
        </DialogHeader>
        {tools.isLoading ? (
          <Skeleton className="h-32 w-full" />
        ) : tools.isError ? (
          <p className="text-sm text-destructive">{tools.error instanceof ApiError ? tools.error.message : 'No se pudo leer el servidor.'}</p>
        ) : !all.length ? (
          <EmptyState>Este servidor no ofrece herramientas.</EmptyState>
        ) : (
          <ul className="space-y-2" aria-label="Herramientas del servidor">
            {all.map((t) => (
              <li key={t.name} className="rounded-lg border p-3">
                <label className="flex cursor-pointer items-start gap-3">
                  <input type="checkbox" className="mt-1" checked={selected.has(t.name)} onChange={(e) => toggle(t.name, e.target.checked)} />
                  <span className="min-w-0 flex-1">
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="font-mono text-sm font-medium">{t.name}</span>
                      {looksLikeWrite(t.name) && <Badge variant="destructive">Puede modificar datos</Badge>}
                    </span>
                    {t.description && <span className="mt-0.5 block text-xs text-muted-foreground">{t.description}</span>}
                  </span>
                </label>
              </li>
            ))}
          </ul>
        )}
        <FieldError message={error ?? undefined} />
        <DialogFooter>
          <Button type="button" disabled={save.isPending || !tools.data} onClick={() => save.mutate()}>
            Guardar ({selected.size})
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function TokenDialog({ botId, connection, onClose, onSaved }: { botId: string; connection: Connection | null; onClose: () => void; onSaved: () => void }) {
  const [token, setToken] = useState('')
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () => patch(`/panel/bots/${botId}/connections/${connection!.id}`, { token }),
    onSuccess: () => {
      setToken('')
      setError(null)
      onSaved()
      onClose()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar el token.'),
  })

  return (
    <Dialog open={!!connection} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Cambiar token de {connection?.name}</DialogTitle>
          <DialogDescription>El token anterior se reemplaza. Se guarda cifrado y no se vuelve a mostrar.</DialogDescription>
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
            <Label htmlFor="mcp-new-token">Token nuevo</Label>
            <Input id="mcp-new-token" type="password" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" />
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
