import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeftIcon, CameraIcon, FileTextIcon, GlobeIcon, MessageCircleIcon, MessagesSquareIcon, PaperclipIcon, SendIcon, Trash2Icon } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { get, post } from '@/lib/api'
import type { ConversationSummary, TranscriptAttachment, TranscriptMessage } from '@/lib/types'

export const CHANNEL: Record<string, { label: string; icon: typeof GlobeIcon; tone: string }> = {
  whatsapp: { label: 'WhatsApp', icon: MessageCircleIcon, tone: 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300' },
  messenger: { label: 'Messenger', icon: MessagesSquareIcon, tone: 'bg-blue-500/15 text-blue-700 dark:text-blue-300' },
  instagram: { label: 'Instagram', icon: CameraIcon, tone: 'bg-pink-500/15 text-pink-700 dark:text-pink-300' },
  web: { label: 'Chat web', icon: GlobeIcon, tone: 'bg-slate-500/15 text-slate-700 dark:text-slate-300' },
}

/**
 * Una conversación a todo lo ancho y alto disponible, como un chat: cabecera con quién es y cómo se atiende, mensajes que ocupan el espacio
 * y la caja de respuesta abajo (Enter envía, Mayús+Enter baja de línea). Se actualiza sola mientras está abierta.
 */
export function ConversationPane({
  botId,
  conversation,
  onBack,
  onDelete,
  onChanged,
}: {
  botId: string
  conversation: ConversationSummary
  onBack?: () => void
  /** Sin él no se ofrece eliminar (el Operador atiende, no borra). */
  onDelete?: () => void
  onChanged?: () => void
}) {
  const queryClient = useQueryClient()
  const [text, setText] = useState('')
  const base = `/panel/bots/${botId}/conversations/${conversation.id}`
  const transcriptKey = ['bot-transcript', botId, conversation.id]
  const messages = useQuery({
    queryKey: transcriptKey,
    queryFn: () => get<{ messages: TranscriptMessage[] }>(base),
    // Así ves lo que el contacto escribe mientras atiendes.
    refetchInterval: 8_000,
  })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['bot-conversations', botId] })
    queryClient.invalidateQueries({ queryKey: transcriptKey })
    queryClient.invalidateQueries({ queryKey: ['bots-attention'] })
    onChanged?.()
  }
  const takeOver = useMutation({ mutationFn: () => post(`${base}/takeover`), onSuccess: refresh, onError: notifyError })
  const release = useMutation({ mutationFn: () => post(`${base}/release`), onSuccess: refresh, onError: notifyError })
  // Respuestas rápidas del chatbot: el servidor arma el texto con tu nombre y el del establecimiento.
  const quick = useMutation({
    mutationFn: (kind: 'greeting' | 'farewell' | 'closing') => post(`${base}/quick-reply`, { kind }),
    onSuccess: refresh,
    onError: notifyError,
  })
  const reply = useMutation({
    mutationFn: () => post(`${base}/reply`, { text }),
    onSuccess: () => {
      setText('')
      refresh()
    },
    onError: notifyError,
  })

  const scroller = useRef<HTMLDivElement>(null)
  // Como en un chat: al abrir o recibir mensajes se va al último.
  useEffect(() => {
    if (scroller.current) scroller.current.scrollTop = scroller.current.scrollHeight
  }, [messages.data, conversation.id])

  const ch = CHANNEL[conversation.channel ?? 'web'] ?? CHANNEL.web
  const Icon = ch.icon
  const isChannel = !!conversation.channel
  const send = () => {
    if (text.trim() && !reply.isPending) reply.mutate()
  }

  return (
    <section className="flex h-full min-h-0 flex-col" aria-label="Conversación">
      <header className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-3 py-3 sm:px-4">
        {onBack && (
          <Button variant="ghost" size="icon" className="lg:hidden" onClick={onBack} aria-label="Volver a la lista">
            <ArrowLeftIcon />
          </Button>
        )}
        <span className={`flex size-10 shrink-0 items-center justify-center rounded-full ${ch.tone}`} title={ch.label}>
          <Icon className="size-5" aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="truncate font-semibold leading-tight">{conversation.contact ?? 'Visitante del chat web'}</h2>
          <p className="truncate text-xs text-muted-foreground">
            {ch.label}
            {conversation.bot_name ? ` · ${conversation.bot_name}` : ''} · {conversation.messages} mensajes
            {(conversation.sessions ?? 1) > 1 ? ` · ${conversation.sessions} conversaciones` : ''}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {conversation.needs_attention && <Badge variant="destructive">{conversation.human_active ? 'Esperando tu respuesta' : 'Necesita atención'}</Badge>}
          {conversation.human_active ? (
            <Button variant="outline" size="sm" onClick={() => release.mutate()} disabled={release.isPending}>
              Devolver al chatbot
            </Button>
          ) : (
            <Button size="sm" onClick={() => takeOver.mutate()} disabled={takeOver.isPending}>
              Tomar la conversación
            </Button>
          )}
          {onDelete && (
            <Button
              variant="ghost"
              size="icon"
              onClick={onDelete}
              aria-label={(conversation.sessions ?? 1) > 1 ? 'Eliminar el historial de este contacto' : 'Eliminar conversación'}
              title={(conversation.sessions ?? 1) > 1 ? 'Eliminar el historial de este contacto' : 'Eliminar conversación'}
            >
              <Trash2Icon />
            </Button>
          )}
        </div>
      </header>

      <div ref={scroller} className="flex min-h-0 flex-1 flex-col gap-1 overflow-y-auto bg-muted/40 p-3 sm:p-5" aria-label="Mensajes de la conversación">
        {messages.isLoading && <Skeleton className="h-20 w-full" />}
        {messages.data?.messages.map((m, i, all) => (
          <Bubble
            key={i}
            message={m}
            previous={all[i - 1]}
            // El archivo se abre por la conversación a la que pertenece: en un hilo con varias, no siempre es la actual.
            fileUrl={(a) => `/panel/bots/${botId}/conversations/${a.conversation_id ?? conversation.id}/attachments/${a.id}`}
          />
        ))}
      </div>

      <footer className="space-y-2 border-t p-3 sm:p-4">
        <p className="text-xs text-muted-foreground">
          {conversation.human_active ? 'Atiende una persona: el chatbot no responde en esta conversación.' : 'Responde el chatbot. Si escribes, él se detiene y sigues tú.'}
        </p>
        {conversation.can_reply && (
          <div className="flex flex-wrap gap-2" role="group" aria-label="Respuestas rápidas">
            <Button type="button" variant="outline" size="sm" disabled={quick.isPending} onClick={() => quick.mutate('greeting')}>Saludo</Button>
            <Button type="button" variant="outline" size="sm" disabled={quick.isPending} onClick={() => quick.mutate('farewell')}>Despedida</Button>
            <Button type="button" variant="outline" size="sm" disabled={quick.isPending} onClick={() => quick.mutate('closing')} title="Envía el cierre y devuelve la conversación al chatbot">Finalizar</Button>
          </div>
        )}
        {conversation.can_reply ? (
          <form
            className="flex items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              send()
            }}
          >
            <Textarea
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                  e.preventDefault()
                  send()
                }
              }}
              maxLength={2000}
              rows={2}
              className="max-h-40 min-h-12 flex-1"
              placeholder="Escribe tu respuesta (Enter envía, Mayús+Enter baja de línea)"
              aria-label="Tu respuesta"
            />
            <Button type="submit" disabled={!text.trim() || reply.isPending} aria-label="Enviar respuesta">
              <SendIcon /> <span className="max-sm:sr-only">Enviar</span>
            </Button>
          </form>
        ) : (
          <p className="text-xs text-muted-foreground">
            {isChannel
              ? 'No puedes escribirle desde aquí: pasaron más de 24 horas desde su último mensaje (o el canal no está activo). Cuando él escriba de nuevo podrás responder.'
              : 'Pasaron más de 24 horas desde el último mensaje del visitante: ya no puedes escribirle desde aquí.'}
          </p>
        )}
      </footer>
    </section>
  )
}

const timeOf = (iso: string) => new Intl.DateTimeFormat('es-CO', { hour: 'numeric', minute: '2-digit' }).format(new Date(iso))
const dayOf = (iso: string) => new Intl.DateTimeFormat('es-CO', { dateStyle: 'long' }).format(new Date(iso))
const sizeOf = (bytes: number) => (bytes >= 1_000_000 ? `${(bytes / 1_000_000).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1000))} KB`)

/**
 * Un mensaje como en un chat real: lo que escribe el contacto va a la izquierda; lo que responde el chatbot o una persona del negocio, a la derecha.
 * El texto se muestra siempre como texto plano (viene del visitante o de la IA): nunca se interpreta como HTML.
 */
export function Bubble({ message: m, previous, fileUrl }: { message: TranscriptMessage; previous?: TranscriptMessage; fileUrl: (a: TranscriptAttachment) => string }) {
  const mine = m.role !== 'user'
  const newDay = !previous || dayOf(previous.created_at) !== dayOf(m.created_at)
  // El contacto volvió a escribir pasadas las 24 horas: es otra conversación del mismo hilo (la unidad con la que se cuenta).
  const newSession = !!previous && !!m.conversation_id && !!previous.conversation_id && m.conversation_id !== previous.conversation_id
  const hasFiles = !!m.attachments?.length
  // El texto «📎 Imagen» solo describe el adjunto: con el archivo a la vista no hace falta repetirlo.
  const text = hasFiles && m.content.startsWith('📎') ? '' : m.content
  return (
    <>
      {newSession && (
        <div className="mt-3 flex items-center gap-3 self-stretch text-xs text-muted-foreground" role="separator" aria-label="Nueva conversación">
          <span className="h-px flex-1 bg-border" />
          Nueva conversación
          <span className="h-px flex-1 bg-border" />
        </div>
      )}
      {newDay && <div className="my-2 self-center rounded-full bg-background px-3 py-0.5 text-xs text-muted-foreground shadow-sm">{dayOf(m.created_at)}</div>}
      <div className={`flex max-w-[85%] flex-col ${mine ? 'items-end self-end' : 'items-start self-start'}`}>
        {mine && <span className="mb-0.5 px-1 text-xs text-muted-foreground">{m.role === 'agent' ? 'Tu equipo' : 'Chatbot'}</span>}
        <div
          className={`whitespace-pre-wrap break-words rounded-2xl px-3 py-2 text-sm shadow-sm ${
            m.role === 'user'
              ? 'rounded-bl-sm bg-background'
              : m.role === 'agent'
                ? 'rounded-br-sm bg-emerald-600 text-white'
                : 'rounded-br-sm bg-primary text-primary-foreground'
          }`}
        >
          {m.attachments?.map((a) => <AttachmentView key={a.id} attachment={a} url={fileUrl(a)} />)}
          {text}
        </div>
        <span className="mt-0.5 px-1 text-[11px] text-muted-foreground">{timeOf(m.created_at)}</span>
      </div>
    </>
  )
}

export function AttachmentView({ attachment: a, url }: { attachment: TranscriptAttachment; url: string }) {
  if (!a.available) {
    return (
      <p className="mb-1 flex items-center gap-1.5 text-xs italic opacity-80">
        <PaperclipIcon className="size-3.5" /> {a.file_name ?? 'Archivo'}: ya no está disponible (los archivos se conservan una semana).
      </p>
    )
  }
  if (a.kind === 'image' || a.kind === 'sticker') {
    return (
      <a href={url} target="_blank" rel="noreferrer" className="mb-1 block">
        <img src={url} alt={a.file_name ?? 'Imagen enviada por el contacto'} loading="lazy" className="max-h-56 max-w-full rounded-lg object-cover" />
      </a>
    )
  }
  return (
    <a href={url} target="_blank" rel="noreferrer" className="mb-1 flex items-center gap-2 rounded-lg bg-black/5 px-2 py-1.5 text-xs underline-offset-2 hover:underline dark:bg-white/10">
      <FileTextIcon className="size-4 shrink-0" />
      <span className="truncate">{a.file_name ?? 'Archivo'}</span>
      <span className="shrink-0 opacity-70">{sizeOf(a.size_bytes)}</span>
    </a>
  )
}
