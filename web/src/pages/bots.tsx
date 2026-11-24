import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PlusIcon } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { EmptyState, FieldError, NativeSelect, PageHeader, formatDate, notifyError } from '@/components/common'
import { BotAvatar } from '@/components/user-avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ApiError, get, patch, post } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import type { Asset, Bot, BotList, Client } from '@/lib/types'

/** Chatbots visibles: todos los de la agencia (o del cliente elegido); un administrador de cliente, los suyos. Se agrupan por asset. */
export default function BotsPage() {
  const navigate = useNavigate()
  const { isAgency } = useAuth()
  const [creating, setCreating] = useState(false)
  const list = useQuery({ queryKey: ['bots', 'all'], queryFn: () => get<BotList>('/panel/bots/') })
  const assets = useQuery({ queryKey: ['assets', 'all'], queryFn: () => get<{ assets: Asset[] }>('/panel/assets') })
  const clients = useQuery({ queryKey: ['clients'], queryFn: () => get<{ clients: Client[] }>('/panel/clients') })
  const bots = list.data?.bots ?? []

  const assetName = (id?: string | null) => assets.data?.assets.find((a) => a.id === id)
  const clientName = (id?: string) => clients.data?.clients.find((c) => c.id === id)?.name

  return (
    <>
      <PageHeader
        title="Chatbots"
        description={isAgency ? 'Todos los chatbots, con el cliente y el asset al que pertenecen.' : 'Los asistentes de tu negocio. Aquí cargas su información y revisas sus conversaciones.'}
        actions={
          isAgency && (
            <Button onClick={() => setCreating(true)} disabled={!assets.data?.assets.length}>
              <PlusIcon /> Nuevo chatbot
            </Button>
          )
        }
      />
      {list.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : !bots.length ? (
        <EmptyState>
          {isAgency
            ? assets.data?.assets.length
              ? 'Todavía no hay chatbots. Crea el primero dentro de un asset.'
              : 'Primero crea un cliente con un asset en Clientes; los chatbots viven dentro de un asset.'
            : 'Todavía no tienes chatbots. Tu agencia los crea y configura.'}
        </EmptyState>
      ) : (
        <BotTable bots={bots} assetLabel={(b) => {
          const asset = assetName(b.asset_id)
          return asset ? `${clientName(asset.client_id) ?? ''} · ${asset.name}` : ''
        }} />
      )}
      <CreateBotDialog open={creating} onOpenChange={setCreating} assets={assets.data?.assets ?? []} clients={clients.data?.clients ?? []} onCreated={(id) => navigate(`/chatbots/${id}`)} />
    </>
  )
}

export function BotTable({ bots, assetLabel }: { bots: Bot[]; assetLabel?: (b: Bot) => string }) {
  const queryClient = useQueryClient()
  const { isAgency } = useAuth()
  const toggle = useMutation({
    mutationFn: (b: Bot) => patch(`/panel/bots/${b.id}`, { is_active: !b.is_active }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['bots'] }),
    onError: notifyError,
  })

  return (
    <div className="rounded-lg border">
      <Table>
        <TableHeader className="max-md:hidden">
          <TableRow>
            <TableHead>Chatbot</TableHead>
            {assetLabel && <TableHead className="hidden md:table-cell">Cliente · asset</TableHead>}
            <TableHead className="hidden lg:table-cell">Creado</TableHead>
            <TableHead className="w-44" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {bots.map((b) => (
            <TableRow key={b.id} className={`max-md:flex max-md:flex-col max-md:gap-2 max-md:p-3 ${b.is_active ? '' : 'opacity-60'}`}>
              <TableCell className="max-md:p-0">
                <div className="flex items-center gap-3">
                  <BotAvatar bot={b} />
                  <div className="min-w-0">
                    <Link to={`/chatbots/${b.id}`} className="font-medium underline-offset-4 hover:underline">
                      {b.name}
                    </Link>
                    <div className="truncate text-xs text-muted-foreground">{b.public_key}</div>
                    {!b.is_active && <Badge variant="outline">Desactivado</Badge>}
                  </div>
                </div>
              </TableCell>
              {assetLabel && <TableCell className="hidden text-sm md:table-cell">{assetLabel(b)}</TableCell>}
              <TableCell className="hidden lg:table-cell">{formatDate(b.created_at)}</TableCell>
              <TableCell className="text-right max-md:p-0 max-md:text-left">
                <Button variant="outline" size="sm" render={<Link to={`/chatbots/${b.id}`} />}>
                  {isAgency ? 'Configurar' : 'Abrir'}
                </Button>
                {isAgency && (
                  <Button variant="ghost" size="sm" disabled={toggle.isPending} onClick={() => toggle.mutate(b)}>
                    {b.is_active ? 'Desactivar' : 'Activar'}
                  </Button>
                )}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  )
}

/** Crea un bot dentro de un asset. Nace con su token de verificación de Meta; la llave de Claude y la app de Meta se ponen en Integraciones. */
export function CreateBotDialog({
  open,
  onOpenChange,
  assets,
  clients,
  fixedAssetId,
  onCreated,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  assets: Asset[]
  clients: Client[]
  fixedAssetId?: string
  onCreated: (id: string) => void
}) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [assetId, setAssetId] = useState('')
  const [error, setError] = useState<string | null>(null)
  const target = fixedAssetId ?? assetId

  const create = useMutation({
    mutationFn: () => post<{ bot: Bot }>('/panel/bots/', { asset_id: target, bot: { name } }),
    onSuccess: (r) => {
      setName('')
      setError(null)
      queryClient.invalidateQueries({ queryKey: ['bots'] })
      queryClient.invalidateQueries({ queryKey: ['assets'] })
      queryClient.invalidateQueries({ queryKey: ['clients'] })
      onOpenChange(false)
      onCreated(r.bot.id)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo crear el chatbot.'),
  })

  const clientName = (id: string) => clients.find((c) => c.id === id)?.name ?? ''

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Nuevo chatbot</DialogTitle>
          <DialogDescription>Después le pones su información, su llave de Claude y, si usará WhatsApp, Messenger o Instagram, su app de Meta.</DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            create.mutate()
          }}
        >
          {!fixedAssetId && (
            <div className="space-y-1.5">
              <Label htmlFor="bot-asset">Asset</Label>
              <NativeSelect id="bot-asset" value={assetId} onChange={(e) => setAssetId(e.target.value)}>
                <option value="">Elige el cliente y el asset</option>
                {assets.map((a) => (
                  <option key={a.id} value={a.id}>
                    {clientName(a.client_id)} · {a.name}
                  </option>
                ))}
              </NativeSelect>
            </div>
          )}
          <div className="space-y-1.5">
            <Label htmlFor="bot-name">Nombre</Label>
            <Input id="bot-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Nombre del chatbot" autoFocus />
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>Cancelar</Button>
            <Button type="submit" disabled={create.isPending || !target || !name.trim()}>
              Crear chatbot
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
