import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { ConfirmDialog, CopyButton, EmptyState, formatDate, notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { del, get } from '@/lib/api'
import type { Bot, ConversationSummary, Lead } from '@/lib/types'

const CHANNEL_LABEL: Record<string, string> = { whatsapp: 'WhatsApp', messenger: 'Messenger', instagram: 'Instagram' }

/** Código para pegar en la web del cliente (una línea) y enlace para ver el chat tal como lo verá el visitante. */
export function InstallPanel({ bot }: { bot: Bot }) {
  const origin = window.location.origin
  const color = bot.color ? ` data-color="${bot.color}"` : ''
  const snippet = `<script src="${origin}/widget.js" data-bot="${bot.public_key}"${color} async></script>`

  return (
    <div className="w-full space-y-5">
      <div className="space-y-2">
        <h3 className="text-sm font-medium">Código de instalación</h3>
        <p className="text-sm text-muted-foreground">
          Pégalo antes de <code>&lt;/body&gt;</code> en todas las páginas donde quieras el chat. Aparece un botón flotante abajo a la derecha.
        </p>
        <div className="flex items-start gap-2">
          <pre className="flex-1 overflow-x-auto rounded-md border bg-muted p-3 text-xs" aria-label="Código de instalación">{snippet}</pre>
          <CopyButton value={snippet} label="Copiar código" />
        </div>
      </div>

      <div className="space-y-1 text-sm">
        <p className="font-medium">Sitios permitidos</p>
        {bot.allowed_origins.length ? (
          <p className="text-muted-foreground">Solo se muestra en: {bot.allowed_origins.join(', ')}.</p>
        ) : (
          <p className="text-muted-foreground">Ahora puede mostrarse en cualquier sitio. Indica los tuyos en Configuración para que nadie más lo use.</p>
        )}
      </div>

      <div className="space-y-1 text-sm">
        <p className="font-medium">Probarlo</p>
        <a className="text-primary underline" href={`/widget/${bot.public_key}`} target="_blank" rel="noreferrer">
          Abrir el chat en una pestaña nueva
        </a>
        {!bot.is_active && <p className="text-destructive">El chatbot está desactivado: los visitantes no pueden usarlo.</p>}
      </div>
    </div>
  )
}

/** Conversaciones del chatbot: cada una se abre a pantalla completa en la Bandeja, donde también se atienden y se responden. */
export function ConversationsPanel({ botId }: { botId: string }) {
  const list = useQuery({ queryKey: ['bot-conversations', botId], queryFn: () => get<{ conversations: ConversationSummary[] }>(`/panel/bots/${botId}/conversations`) })
  const rows = list.data?.conversations ?? []
  return (
    <div className="space-y-4">
      <p className="max-w-xl text-sm text-muted-foreground">Las últimas conversaciones. Se abren en la Bandeja, a pantalla completa. Se borran solas pasado el periodo de retención, o antes si las eliminas.</p>
      {list.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : !rows.length ? (
        <EmptyState>Todavía nadie ha conversado con este chatbot.</EmptyState>
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Primera pregunta</TableHead>
                <TableHead className="hidden md:table-cell">Canal</TableHead>
                <TableHead className="text-right">Mensajes</TableHead>
                <TableHead className="hidden md:table-cell">Última actividad</TableHead>
                <TableHead className="w-32" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((c) => (
                <TableRow key={c.id}>
                  <TableCell className="max-w-xs truncate">
                    {c.first_message ?? '—'}
                    {(c.sessions ?? 1) > 1 && <Badge variant="outline" className="ml-2">{c.sessions} conversaciones</Badge>}
                    {c.human_active && <Badge variant="secondary" className="ml-2">Atiende una persona</Badge>}
                    {c.needs_attention && <Badge variant="destructive" className="ml-2">{c.human_active ? 'Esperando tu respuesta' : 'Necesita atención'}</Badge>}
                  </TableCell>
                  <TableCell className="hidden md:table-cell">
                    {c.channel ? (
                      <>
                        <div>{CHANNEL_LABEL[c.channel] ?? c.channel}</div>
                        <div className="text-xs text-muted-foreground">{c.contact}</div>
                      </>
                    ) : (
                      'Web'
                    )}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{c.messages}</TableCell>
                  <TableCell className="hidden md:table-cell">{formatDate(c.last_message_at)}</TableCell>
                  <TableCell className="text-right">
                    <Button variant="ghost" size="sm" render={<Link to={`/bandeja?bot=${botId}&c=${c.id}`} />} aria-label={`Abrir conversación ${c.first_message ?? ''}`}>
                      Abrir
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  )
}

/** Personas que dejaron sus datos en el chat (con su consentimiento). */
export function LeadsPanel({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const [removing, setRemoving] = useState<Lead | null>(null)
  const key = ['bot-leads', botId]
  const list = useQuery({ queryKey: key, queryFn: () => get<{ leads: Lead[] }>(`/panel/bots/${botId}/leads`) })

  const remove = useMutation({
    mutationFn: (id: string) => del(`/panel/bots/${botId}/leads/${id}`),
    onSuccess: () => {
      setRemoving(null)
      queryClient.invalidateQueries({ queryKey: key })
    },
    onError: notifyError,
  })

  const rows = list.data?.leads ?? []
  return (
    <div className="space-y-4">
      <p className="max-w-xl text-sm text-muted-foreground">Contactos que los visitantes dejaron al chatear. Úsalos solo para lo que autorizaron.</p>
      {list.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : !rows.length ? (
        <EmptyState>Todavía no hay contactos.</EmptyState>
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Nombre</TableHead>
                <TableHead>Contacto</TableHead>
                <TableHead className="hidden md:table-cell">Interés</TableHead>
                <TableHead className="hidden lg:table-cell">Fecha</TableHead>
                <TableHead className="w-24" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((l) => (
                <TableRow key={l.id}>
                  <TableCell className="font-medium">{l.name}</TableCell>
                  <TableCell>
                    {l.email && <div>{l.email}</div>}
                    {l.phone && <div className="text-xs text-muted-foreground">{l.phone}</div>}
                  </TableCell>
                  <TableCell className="hidden max-w-xs truncate md:table-cell">{l.interest ?? '—'}</TableCell>
                  <TableCell className="hidden lg:table-cell">{formatDate(l.created_at)}</TableCell>
                  <TableCell className="text-right">
                    <Button variant="ghost" size="sm" onClick={() => setRemoving(l)} aria-label={`Eliminar contacto ${l.name}`}>
                      Eliminar
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Eliminar contacto"
        description={`Se borrarán los datos de ${removing?.name ?? ''}. No se puede deshacer.`}
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing.id)}
      />
    </div>
  )
}
