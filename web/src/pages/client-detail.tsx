import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeftIcon, PlusIcon } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { ClientAppPanel } from '@/components/client-app-panel'
import { ColorPicker } from '@/components/color-picker'
import { ConfirmDialog, EmptyState, FieldError, NativeSelect, PageHeader, notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, del, get, patch, post } from '@/lib/api'
import { setClientScope } from '@/lib/client-scope'
import { type Asset, type BotList, type Client, WIDGET_TEXTS } from '@/lib/types'
import { BotTable, CreateBotDialog } from '@/pages/bots'

/** Ficha de un cliente (solo la agencia): assets y chatbots, marca del widget, precio y topes. */
export default function ClientDetailPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const client = useQuery({ queryKey: ['client', id], queryFn: () => get<{ client: Client }>(`/panel/clients/${id}`), enabled: !!id })
  const assets = useQuery({ queryKey: ['assets', id], queryFn: () => get<{ assets: Asset[] }>(`/panel/assets?clientId=${id}`), enabled: !!id })
  const bots = useQuery({ queryKey: ['bots', 'all'], queryFn: () => get<BotList>('/panel/bots/') })

  if (client.isLoading || !client.data) {
    return client.isError ? <p className="text-sm text-muted-foreground">No se encontró este cliente.</p> : <Skeleton className="h-64 w-full" />
  }

  const c = client.data.client
  const workIn = () => {
    setClientScope(c.id)
    void queryClient.invalidateQueries({ predicate: (q) => q.queryKey[0] !== 'client-options' && q.queryKey[0] !== 'me' })
    navigate('/equipo')
  }

  return (
    <>
      <Button variant="ghost" size="sm" render={<Link to="/clientes" />}>
        <ArrowLeftIcon /> Clientes
      </Button>
      <PageHeader
        title={c.name}
        description={c.status === 'suspended' ? 'Suspendido: sus chatbots no atienden y su equipo no puede entrar.' : `${c.assets} assets · ${c.bots} chatbots · ${c.conversations_this_month.toLocaleString('es-CO')} conversaciones este mes`}
        actions={
          <Button variant="outline" onClick={workIn}>
            Gestionar su equipo
          </Button>
        }
      />
      <Tabs defaultValue="assets">
        <TabsList className="h-auto! max-w-full flex-wrap justify-start">
          <TabsTrigger value="assets">Assets y chatbots</TabsTrigger>
          <TabsTrigger value="data">Datos</TabsTrigger>
          <TabsTrigger value="brand">Marca del widget</TabsTrigger>
          <TabsTrigger value="app">App del cliente</TabsTrigger>
          <TabsTrigger value="billing">Precio y topes</TabsTrigger>
        </TabsList>
        <TabsContent value="assets" className="pt-4">
          <AssetsPanel client={c} assets={assets.data?.assets ?? []} bots={bots.data?.bots ?? []} loading={assets.isLoading} />
        </TabsContent>
        <TabsContent value="data" className="pt-4">
          <DataForm key={c.id} client={c} />
        </TabsContent>
        <TabsContent value="brand" className="pt-4">
          <BrandForm key={c.id} client={c} />
        </TabsContent>
        <TabsContent value="app" className="pt-4">
          <ClientAppPanel client={c} />
        </TabsContent>
        <TabsContent value="billing" className="pt-4">
          <BillingForm key={c.id} client={c} />
        </TabsContent>
      </Tabs>
    </>
  )
}

function useSaveClient(client: Client) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: Record<string, unknown>) => patch<{ client: Client }>(`/panel/clients/${client.id}`, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['client', client.id] })
      queryClient.invalidateQueries({ queryKey: ['clients'] })
      queryClient.invalidateQueries({ queryKey: ['client-options'] })
      toast.success('Cambios guardados.')
    },
  })
}

function AssetsPanel({ client, assets, bots, loading }: { client: Client; assets: Asset[]; bots: BotList['bots']; loading: boolean }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [creatingAsset, setCreatingAsset] = useState(false)
  const [botFor, setBotFor] = useState<Asset | null>(null)
  const [editing, setEditing] = useState<Asset | null>(null)
  const [removing, setRemoving] = useState<Asset | null>(null)

  const remove = useMutation({
    mutationFn: (a: Asset) => del(`/panel/assets/${a.id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['assets'] })
      queryClient.invalidateQueries({ queryKey: ['client', client.id] })
      setRemoving(null)
    },
    onError: notifyError,
  })

  if (loading) return <Skeleton className="h-32 w-full" />

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">Un asset agrupa los chatbots de una marca, sede o línea de negocio del cliente.</p>
        <Button onClick={() => setCreatingAsset(true)}>
          <PlusIcon /> Nuevo asset
        </Button>
      </div>
      {assets.length === 0 ? (
        <EmptyState>Este cliente todavía no tiene assets. Crea uno para poder agregarle chatbots.</EmptyState>
      ) : (
        assets.map((a) => {
          const own = bots.filter((b) => b.asset_id === a.id)
          return (
            <Card key={a.id}>
              <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
                <div>
                  <CardTitle>{a.name}</CardTitle>
                  {a.description && <CardDescription>{a.description}</CardDescription>}
                </div>
                <div className="flex flex-wrap gap-1">
                  <Button size="sm" onClick={() => setBotFor(a)}>
                    <PlusIcon /> Chatbot
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => setEditing(a)}>Editar</Button>
                  <Button size="sm" variant="ghost" onClick={() => setRemoving(a)} disabled={own.length > 0} title={own.length > 0 ? 'Borra o mueve sus chatbots primero' : undefined}>
                    Eliminar
                  </Button>
                </div>
              </CardHeader>
              <CardContent>{own.length ? <BotTable bots={own} /> : <p className="text-sm text-muted-foreground">Sin chatbots todavía.</p>}</CardContent>
            </Card>
          )
        })
      )}

      <AssetDialog clientId={client.id} asset={null} open={creatingAsset} onOpenChange={setCreatingAsset} />
      <AssetDialog clientId={client.id} asset={editing} open={!!editing} onOpenChange={(o) => !o && setEditing(null)} />
      <CreateBotDialog
        open={!!botFor}
        onOpenChange={(o) => !o && setBotFor(null)}
        assets={assets}
        clients={[client]}
        fixedAssetId={botFor?.id}
        onCreated={(id) => navigate(`/chatbots/${id}`)}
      />
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Eliminar asset"
        description={`Se eliminará "${removing?.name}". Solo se puede si no tiene chatbots.`}
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </div>
  )
}

function AssetDialog({ clientId, asset, open, onOpenChange }: { clientId: string; asset: Asset | null; open: boolean; onOpenChange: (o: boolean) => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(asset?.name ?? '')
  const [description, setDescription] = useState(asset?.description ?? '')
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () =>
      asset ? patch(`/panel/assets/${asset.id}`, { name, description }) : post('/panel/assets', { client_id: clientId, name, description }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['assets'] })
      queryClient.invalidateQueries({ queryKey: ['client', clientId] })
      queryClient.invalidateQueries({ queryKey: ['clients'] })
      setError(null)
      if (!asset) {
        setName('')
        setDescription('')
      }
      onOpenChange(false)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar el asset.'),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{asset ? 'Editar asset' : 'Nuevo asset'}</DialogTitle>
          <DialogDescription>Por ejemplo, cada restaurante o cada marca del cliente.</DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            save.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="asset-name">Nombre</Label>
            <Input id="asset-name" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="asset-description">Descripción (opcional)</Label>
            <Textarea id="asset-description" value={description} onChange={(e) => setDescription(e.target.value)} rows={2} />
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>Cancelar</Button>
            <Button type="submit" disabled={save.isPending || !name.trim()}>Guardar</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function DataForm({ client }: { client: Client }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const save = useSaveClient(client)
  const [name, setName] = useState(client.name)
  const [slug, setSlug] = useState(client.slug)
  const [status, setStatus] = useState(client.status)
  const [deleting, setDeleting] = useState(false)
  const remove = useMutation({
    mutationFn: () => del(`/panel/clients/${client.id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['clients'] })
      queryClient.invalidateQueries({ queryKey: ['client-options'] })
      navigate('/clientes', { replace: true })
    },
    onError: notifyError,
  })

  return (
    <form
      className="grid max-w-2xl gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate({ name, slug, status })
      }}
    >
      <div className="space-y-1.5">
        <Label htmlFor="client-name">Nombre</Label>
        <Input id="client-name" value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="client-slug">Nombre corto</Label>
        <Input id="client-slug" value={slug} onChange={(e) => setSlug(e.target.value)} />
        <p className="text-xs text-muted-foreground">Minúsculas, números y guiones. Identifica al cliente de forma única.</p>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="client-status">Estado</Label>
        <NativeSelect id="client-status" value={status} onChange={(e) => setStatus(e.target.value as Client['status'])}>
          <option value="active">Activo</option>
          <option value="suspended">Suspendido (sus chatbots dejan de atender y su equipo no entra)</option>
        </NativeSelect>
      </div>
      <FieldError message={save.error instanceof ApiError ? save.error.message : undefined} />
      <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={save.isPending}>Guardar cambios</Button>
        <Button type="button" variant="outline" onClick={() => setDeleting(true)}>Eliminar cliente</Button>
      </div>
      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title="Eliminar cliente"
        description={`Solo se puede eliminar un cliente sin assets ni usuarios. Para dejar de atender a "${client.name}" sin perder datos, suspéndelo.`}
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </form>
  )
}

/** Marca del widget del cliente: lo que quede vacío usa la de la agencia, y el color de cada bot manda sobre todo. */
function BrandForm({ client }: { client: Client }) {
  const save = useSaveClient(client)
  const [logo, setLogo] = useState(client.logo_url ?? '')
  const [color, setColor] = useState(client.primary_color ?? '')
  const [texts, setTexts] = useState<Record<string, string>>(client.texts ?? {})

  return (
    <form
      className="grid max-w-3xl gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate({ logo_url: logo, primary_color: color, texts })
      }}
    >
      <p className="text-sm text-muted-foreground">Lo que dejes vacío usa la marca de la agencia. El color propio de un chatbot manda sobre este.</p>
      <div className="space-y-1.5">
        <Label htmlFor="brand-logo">Logo (dirección https)</Label>
        <Input id="brand-logo" value={logo} onChange={(e) => setLogo(e.target.value)} placeholder="https://…/logo.png" />
      </div>
      <div className="space-y-1.5">
        <Label id="brand-color-label">Color principal</Label>
        <ColorPicker id="brand-color" label="Color del widget" value={color} onChange={setColor} />
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        {WIDGET_TEXTS.map((t) => (
          <div key={t.key} className="space-y-1.5">
            <Label htmlFor={`brand-text-${t.key}`}>{t.label}</Label>
            <Input id={`brand-text-${t.key}`} value={texts[t.key] ?? ''} placeholder={t.placeholder} onChange={(e) => setTexts({ ...texts, [t.key]: e.target.value })} />
          </div>
        ))}
      </div>
      <FieldError message={save.error instanceof ApiError ? save.error.message : undefined} />
      <div>
        <Button type="submit" disabled={save.isPending}>Guardar marca</Button>
      </div>
    </form>
  )
}

/** Precio por conversación y topes. Todo vacío = sin cobro e ilimitado (así arranca cada cliente). */
function BillingForm({ client }: { client: Client }) {
  const save = useSaveClient(client)
  const [price, setPrice] = useState(client.price_per_conversation?.toString() ?? '')
  const [currency, setCurrency] = useState(client.currency)
  const [monthly, setMonthly] = useState(client.monthly_conversation_cap?.toString() ?? '')
  const [perConversation, setPerConversation] = useState(client.messages_per_conversation_cap?.toString() ?? '')
  const num = (v: string) => (v.trim() === '' ? 0 : Number(v.replace(',', '.')))

  return (
    <form
      className="grid max-w-2xl gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate({ price_per_conversation: num(price), currency, monthly_conversation_cap: num(monthly), messages_per_conversation_cap: num(perConversation) })
      }}
    >
      <Card>
        <CardHeader>
          <CardTitle>Así está hoy</CardTitle>
          <CardDescription>
            {client.price_per_conversation === null ? 'Sin cobro por conversación' : `${client.price_per_conversation} ${client.currency} por conversación`} ·{' '}
            {client.monthly_conversation_cap === null ? 'conversaciones ilimitadas' : `hasta ${client.monthly_conversation_cap.toLocaleString('es-CO')} conversaciones al mes`} ·{' '}
            {client.messages_per_conversation_cap === null ? 'mensajes por conversación sin tope' : `hasta ${client.messages_per_conversation_cap} mensajes por conversación`}
          </CardDescription>
        </CardHeader>
      </Card>
      <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
        <div className="space-y-1.5">
          <Label htmlFor="bill-price">Precio por conversación</Label>
          <Input id="bill-price" inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} placeholder="Vacío = no se cobra" />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="bill-currency">Moneda</Label>
          <Input id="bill-currency" value={currency} maxLength={3} onChange={(e) => setCurrency(e.target.value.toUpperCase())} />
        </div>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="bill-monthly">Tope de conversaciones al mes</Label>
        <Input id="bill-monthly" inputMode="numeric" value={monthly} onChange={(e) => setMonthly(e.target.value)} placeholder="Vacío = ilimitado" />
        <p className="text-xs text-muted-foreground">Suma las conversaciones de todos los chatbots del cliente. Al alcanzarlo, no se abren conversaciones nuevas hasta el mes siguiente.</p>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="bill-per">Tope de mensajes por conversación</Label>
        <Input id="bill-per" inputMode="numeric" value={perConversation} onChange={(e) => setPerConversation(e.target.value)} placeholder="Vacío = sin tope" />
      </div>
      <FieldError message={save.error instanceof ApiError ? save.error.message : undefined} />
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={save.isPending}>Guardar</Button>
        {client.monthly_conversation_cap === null && <Badge variant="secondary">Ilimitado</Badge>}
      </div>
    </form>
  )
}
