import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PlusIcon } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { EmptyState, FieldError, PageHeader } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ApiError, get, post } from '@/lib/api'
import type { Client } from '@/lib/types'

export const capLabel = (cap: number | null) => (cap === null ? 'Ilimitado' : cap.toLocaleString('es-CO'))

/** Clientes de la agencia: cada uno con sus assets, chatbots, equipo, marca del widget y, si se quiere, precio y topes. */
export default function ClientsPage() {
  const navigate = useNavigate()
  const [creating, setCreating] = useState(false)
  const clients = useQuery({ queryKey: ['clients'], queryFn: () => get<{ clients: Client[] }>('/panel/clients') })
  const list = clients.data?.clients ?? []

  return (
    <>
      <PageHeader
        title="Clientes"
        description="Cada cliente tiene sus assets (marcas o sedes) y dentro sus chatbots. Sin topes configurados, un cliente es ilimitado."
        actions={
          <Button onClick={() => setCreating(true)}>
            <PlusIcon /> Nuevo cliente
          </Button>
        }
      />
      {clients.isLoading ? (
        <Skeleton className="h-40 w-full" />
      ) : list.length === 0 ? (
        <EmptyState>Todavía no hay clientes. Crea el primero para empezar a montar sus chatbots.</EmptyState>
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Cliente</TableHead>
                <TableHead className="hidden sm:table-cell">Assets</TableHead>
                <TableHead className="hidden sm:table-cell">Chatbots</TableHead>
                <TableHead>Conversaciones del mes</TableHead>
                <TableHead className="hidden md:table-cell">Tope mensual</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.map((c) => (
                <TableRow key={c.id} className="cursor-pointer" onClick={() => navigate(`/clientes/${c.id}`)}>
                  <TableCell>
                    <Link to={`/clientes/${c.id}`} className="font-medium hover:underline" onClick={(e) => e.stopPropagation()}>
                      {c.name}
                    </Link>
                    {c.status === 'suspended' && <Badge variant="outline" className="ml-2">Suspendido</Badge>}
                    <div className="text-xs text-muted-foreground">{c.slug}</div>
                  </TableCell>
                  <TableCell className="hidden tabular-nums sm:table-cell">{c.assets}</TableCell>
                  <TableCell className="hidden tabular-nums sm:table-cell">{c.bots}</TableCell>
                  <TableCell className="tabular-nums">{c.conversations_this_month.toLocaleString('es-CO')}</TableCell>
                  <TableCell className="hidden md:table-cell">{capLabel(c.monthly_conversation_cap)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      <CreateClientDialog open={creating} onOpenChange={setCreating} onCreated={(id) => navigate(`/clientes/${id}`)} />
    </>
  )
}

function CreateClientDialog({ open, onOpenChange, onCreated }: { open: boolean; onOpenChange: (o: boolean) => void; onCreated: (id: string) => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [asset, setAsset] = useState('')
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: async () => {
      const { client } = await post<{ client: Client }>('/panel/clients', { name })
      // El primer asset se crea de una vez: casi siempre el cliente arranca con una marca o sede.
      if (asset.trim()) await post('/panel/assets', { client_id: client.id, name: asset })
      return client
    },
    onSuccess: (client) => {
      queryClient.invalidateQueries({ queryKey: ['clients'] })
      queryClient.invalidateQueries({ queryKey: ['client-options'] })
      setName('')
      setAsset('')
      setError(null)
      onOpenChange(false)
      onCreated(client.id)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo crear el cliente.'),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Nuevo cliente</DialogTitle>
          <DialogDescription>Precio, topes y marca del widget se configuran después en la ficha del cliente. Sin topes, es ilimitado.</DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            create.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="client-name">Nombre del cliente</Label>
            <Input id="client-name" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="client-asset">Primer asset (opcional)</Label>
            <Input id="client-asset" value={asset} onChange={(e) => setAsset(e.target.value)} placeholder="Marca, sede o línea de negocio" />
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>Cancelar</Button>
            <Button type="submit" disabled={create.isPending || !name.trim()}>Crear cliente</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
