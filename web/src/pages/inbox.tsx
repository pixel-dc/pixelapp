import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BotIcon, InboxIcon, MessagesSquareIcon, UserIcon, UserRoundIcon } from 'lucide-react'
import { useSearchParams } from 'react-router'
import { useState } from 'react'
import { ConfirmDialog, EmptyState, PageHeader, formatDate, notifyError } from '@/components/common'
import { CHANNEL, ConversationPane } from '@/components/conversation-pane'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { del, get } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import type { ConversationSummary } from '@/lib/types'

type Filter = 'attention' | 'all'
type Kind = 'all' | 'human' | 'bot'

/** Insignia del canal: color de marca, para distinguirlo de un vistazo sobre el avatar. */
const BADGE: Record<string, string> = {
  whatsapp: 'bg-emerald-500 text-white',
  messenger: 'bg-blue-500 text-white',
  instagram: 'bg-gradient-to-tr from-amber-400 via-pink-500 to-purple-600 text-white',
  web: 'bg-slate-500 text-white',
}

/**
 * Todas las conversaciones de todos los chatbots y canales, a pantalla completa: la lista a la izquierda y la conversación a la derecha
 * (en el teléfono, una u otra). Lo que espera a una persona va primero. La conversación elegida queda en la dirección (?c=…).
 */
export default function InboxPage() {
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const selectedId = params.get('c')
  const botParam = params.get('bot')
  const [filter, setFilter] = useState<Filter>('all')
  const [kind, setKind] = useState<Kind>('all')
  const [channel, setChannel] = useState<string | null>(null)
  const { isOperator } = useAuth()
  const [removing, setRemoving] = useState<ConversationSummary | null>(null)

  const list = useQuery({
    queryKey: ['inbox', 'all'],
    queryFn: () => get<{ conversations: ConversationSummary[] }>('/panel/bots/inbox?filter=all'),
    refetchInterval: 15_000,
  })
  const all = list.data?.conversations ?? []
  const chOf = (c: ConversationSummary) => c.channel ?? 'web'
  const count = (fn: (c: ConversationSummary) => boolean) => all.filter(fn).length
  const rows = all.filter(
    (c) => (filter === 'all' || c.needs_attention) && (kind === 'all' || (kind === 'human') === !!c.human_active) && (!channel || chOf(c) === channel),
  )
  const inList = rows.find((c) => c.id === selectedId)
  // Si se llega desde la ficha de un chatbot con una conversación antigua que no está entre las más recientes, se busca en la de ese bot.
  const fallback = useQuery({
    queryKey: ['bot-conversations', botParam],
    queryFn: () => get<{ conversations: ConversationSummary[] }>(`/panel/bots/${botParam}/conversations`),
    enabled: !!selectedId && !!botParam && !inList && !list.isLoading,
  })
  const selected = inList ?? fallback.data?.conversations.find((c) => c.id === selectedId) ?? null
  const botId = selected?.bot_id ?? botParam

  const select = (c: ConversationSummary | null) =>
    setParams(c ? { bot: c.bot_id ?? '', c: c.id } : {}, { replace: true })

  const remove = useMutation({
    mutationFn: (c: ConversationSummary) => del(`/panel/bots/${c.bot_id ?? botId}/conversations/${c.id}`),
    onSuccess: () => {
      setRemoving(null)
      select(null)
      queryClient.invalidateQueries({ queryKey: ['inbox'] })
      queryClient.invalidateQueries({ queryKey: ['bots-attention'] })
    },
    onError: notifyError,
  })

  const open = !!selected
  return (
    <>
      <div className={open ? 'max-lg:hidden' : ''}>
        <PageHeader title="Bandeja" description="Las conversaciones de todos tus chatbots y canales. Las que esperan a una persona van primero." />
      </div>

      <div
        className={`grid min-h-[28rem] overflow-hidden rounded-xl border bg-card shadow-sm lg:grid-cols-[auto_1fr] ${
          open ? 'h-[calc(100dvh-8rem)] lg:h-[calc(100dvh-10rem)]' : 'h-[calc(100dvh-13rem)] lg:h-[calc(100dvh-10rem)]'
        }`}
      >
        <div className={`flex min-h-0 flex-col lg:flex-row ${open ? 'max-lg:hidden' : ''}`}>
        <nav aria-label="Filtros de la bandeja" className="flex shrink-0 gap-1 overflow-x-auto border-b p-2 lg:w-48 lg:flex-col lg:overflow-visible lg:border-b-0 lg:border-r lg:p-3">
          <RailItem icon={InboxIcon} label="Todas" n={all.length} active={filter === 'all' && !channel} onClick={() => { setFilter('all'); setChannel(null) }} />
          <RailItem icon={UserRoundIcon} label="Necesitan atención" n={count((c) => c.needs_attention)} active={filter === 'attention' && !channel} onClick={() => { setFilter('attention'); setChannel(null) }} />
          <p className="hidden px-2 pt-3 text-[11px] font-medium uppercase tracking-wide text-muted-foreground lg:block">Canales</p>
          {Object.entries(CHANNEL).map(([key, ch]) => {
            const Icon = ch.icon
            return (
              <RailItem
                key={key}
                icon={Icon}
                label={ch.label}
                n={count((c) => chOf(c) === key)}
                active={channel === key}
                onClick={() => { setChannel(channel === key ? null : key); setFilter('all') }}
              />
            )
          })}
        </nav>

        <aside className="flex min-h-0 min-w-0 flex-1 flex-col lg:w-80 lg:flex-none lg:border-r" aria-label="Lista de conversaciones">
          <div role="tablist" aria-label="Tipo de conversación" className="m-3 inline-flex self-start rounded-lg bg-muted p-1 text-sm">
            {(
              [
                ['all', 'Todas', null],
                ['human', 'Humano', UserIcon],
                ['bot', 'Bot', BotIcon],
              ] as const
            ).map(([value, label, Icon]) => (
              <button
                key={value}
                role="tab"
                type="button"
                aria-selected={kind === value}
                onClick={() => setKind(value)}
                className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 font-medium transition ${kind === value ? 'bg-background shadow-sm' : 'text-muted-foreground hover:text-foreground'}`}
              >
                {Icon && <Icon className="size-3.5" aria-hidden />}
                {label}
              </button>
            ))}
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto">
            {list.isLoading ? (
              <div className="space-y-2 p-3">
                <Skeleton className="h-16 w-full" />
                <Skeleton className="h-16 w-full" />
              </div>
            ) : !rows.length ? (
              <div className="p-3">
                <EmptyState>{filter === 'attention' && !channel && kind === 'all' ? 'Nada espera a una persona ahora mismo. 🎉' : all.length ? 'Ninguna conversación coincide con este filtro.' : 'Todavía no hay conversaciones.'}</EmptyState>
              </div>
            ) : (
              <ul className="divide-y" aria-label="Conversaciones">
                {rows.map((c) => {
                  const ch = CHANNEL[c.channel ?? 'web'] ?? CHANNEL.web
                  const Icon = ch.icon
                  const active = c.id === selectedId
                  return (
                    <li key={c.id}>
                      <button
                        type="button"
                        onClick={() => select(c)}
                        aria-current={active ? 'true' : undefined}
                        className={`flex w-full items-start gap-3 p-3 text-left transition hover:bg-muted/50 focus-visible:bg-muted/50 focus-visible:outline-none ${active ? 'bg-accent' : ''}`}
                      >
                        <span className="relative mt-0.5 flex size-10 shrink-0 items-center justify-center rounded-full bg-muted text-muted-foreground" title={ch.label}>
                          <UserIcon className="size-5" aria-hidden />
                          <span className={`absolute -bottom-0.5 -right-0.5 flex size-[18px] items-center justify-center rounded-full ring-2 ring-card ${BADGE[c.channel ?? 'web'] ?? BADGE.web}`}>
                            <Icon className="size-2.5" aria-hidden />
                          </span>
                          <span className="sr-only">{ch.label}</span>
                        </span>
                        <span className="min-w-0 flex-1">
                          <span className="flex items-center justify-between gap-2">
                            <span className="truncate font-medium">{c.contact ?? 'Visitante del chat web'}</span>
                            <span className="shrink-0 text-[11px] text-muted-foreground">{formatDate(c.last_message_at)}</span>
                          </span>
                          <span className="mt-0.5 block truncate text-sm text-muted-foreground">{c.first_message ?? '—'}</span>
                          <span className="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
                            <span className="truncate">{c.bot_name}</span>
                            {c.needs_attention && <Badge variant="destructive">{c.human_active ? 'Esperando tu respuesta' : 'Necesita atención'}</Badge>}
                            {c.human_active && !c.needs_attention && <Badge variant="secondary">Atiende una persona</Badge>}
                          </span>
                        </span>
                      </button>
                    </li>
                  )
                })}
              </ul>
            )}
          </div>
        </aside>
        </div>

        <div className={`min-h-0 min-w-0 ${open ? '' : 'max-lg:hidden'}`}>
          {selected && botId ? (
            <ConversationPane
              botId={botId}
              conversation={selected}
              onBack={() => select(null)}
              onDelete={isOperator ? undefined : () => setRemoving(selected)}
              onChanged={() => queryClient.invalidateQueries({ queryKey: ['inbox'] })}
            />
          ) : selectedId && (list.isLoading || fallback.isLoading) ? (
            <div className="p-6">
              <Skeleton className="h-24 w-full" />
            </div>
          ) : (
            <div className="grid h-full place-items-center p-6 text-center text-muted-foreground">
              <div className="space-y-2">
                <MessagesSquareIcon className="mx-auto size-10 opacity-40" aria-hidden />
                <p className="font-medium text-foreground">{selectedId ? 'No encontramos esa conversación' : 'Elige una conversación'}</p>
                <p className="text-sm">{selectedId ? 'Puede haberse eliminado o ya no estar entre las recientes.' : 'Aquí la ves completa, la atiendes y le respondes.'}</p>
              </div>
            </div>
          )}
        </div>
      </div>

      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Eliminar conversación"
        description="Se borran los mensajes de esta conversación y sus archivos. No se puede deshacer."
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </>
  )
}

function RailItem({ icon: Icon, label, n, active, onClick }: { icon: typeof InboxIcon; label: string; n: number; active: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? 'true' : undefined}
      className={`flex shrink-0 items-center gap-2 rounded-md px-2.5 py-1.5 text-sm transition ${active ? 'bg-accent font-medium text-accent-foreground' : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground'}`}
    >
      <Icon className="size-4 shrink-0" aria-hidden />
      <span className="truncate">{label}</span>
      <span className="ml-auto pl-2 text-xs tabular-nums">{n}</span>
    </button>
  )
}
