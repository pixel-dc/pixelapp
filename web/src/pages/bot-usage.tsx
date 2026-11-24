import { useQuery } from '@tanstack/react-query'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { get } from '@/lib/api'
import type { BotUsage, BotUsageTotals } from '@/lib/types'

const number = (n: number) => n.toLocaleString('es-CO')
/** El costo llega en millonésimas de dólar. */
export const usd = (micros: number) => `US$ ${(micros / 1_000_000).toLocaleString('es-CO', { minimumFractionDigits: 4, maximumFractionDigits: 4 })}`

const CHANNEL_LABEL: Record<string, string> = { web: 'Chat web', whatsapp: 'WhatsApp', messenger: 'Messenger', instagram: 'Instagram' }

function Summary({ title, totals }: { title: string; totals: BotUsageTotals }) {
  const perReply = totals.replies > 0 ? totals.cost_micros / totals.replies : 0
  return (
    <div className="rounded-lg border p-4">
      <p className="text-sm text-muted-foreground">{title}</p>
      <p className="mt-1 text-2xl font-semibold">{usd(totals.cost_micros)}</p>
      <dl className="mt-2 space-y-0.5 text-sm text-muted-foreground">
        <div className="flex justify-between"><dt>Respuestas con IA</dt><dd>{number(totals.replies)}</dd></div>
        <div className="flex justify-between"><dt>Tokens de entrada</dt><dd>{number(totals.tokens_in)}</dd></div>
        <div className="flex justify-between"><dt>Tokens de salida</dt><dd>{number(totals.tokens_out)}</dd></div>
        <div className="flex justify-between"><dt>Costo por respuesta</dt><dd>{totals.replies > 0 ? usd(perReply) : '—'}</dd></div>
      </dl>
    </div>
  )
}

export function UsagePanel({ botId }: { botId: string }) {
  const q = useQuery({ queryKey: ['bot-usage', botId], queryFn: () => get<{ usage: BotUsage }>(`/panel/bots/${botId}/usage`) })
  if (q.isLoading) return <Skeleton className="h-48 w-full" />
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">No se pudo cargar el consumo.</p>
  const u = q.data.usage

  return (
    <div className="space-y-6">
      <div className="grid gap-4 md:grid-cols-3">
        <Summary title="Este mes" totals={u.month} />
        <Summary title="Últimos 30 días" totals={u.last_30_days} />
        <Summary title={`Todo lo registrado (${u.retention_days} días)`} totals={u.retained} />
      </div>
      <p className="text-xs text-muted-foreground">
        Costo aproximado: se calcula con US$ {u.input_usd_per_million} por millón de tokens de entrada y US$ {u.output_usd_per_million} por millón de salida.
        El cobro real del proveedor puede variar un poco. Solo cuentan las respuestas generadas con IA.
      </p>

      <section className="space-y-2">
        <h3 className="text-sm font-medium">Por canal</h3>
        {u.by_channel.length === 0 ? (
          <p className="text-sm text-muted-foreground">Todavía no hay respuestas con IA.</p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Canal</TableHead>
                <TableHead className="text-right">Respuestas</TableHead>
                <TableHead className="hidden text-right md:table-cell">Tokens entrada</TableHead>
                <TableHead className="hidden text-right md:table-cell">Tokens salida</TableHead>
                <TableHead className="text-right">Costo</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {u.by_channel.map((c) => (
                <TableRow key={c.channel}>
                  <TableCell>{CHANNEL_LABEL[c.channel] ?? c.channel}</TableCell>
                  <TableCell className="text-right">{number(c.replies)}</TableCell>
                  <TableCell className="hidden text-right md:table-cell">{number(c.tokens_in)}</TableCell>
                  <TableCell className="hidden text-right md:table-cell">{number(c.tokens_out)}</TableCell>
                  <TableCell className="text-right">{usd(c.cost_micros)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>

      {u.days.length > 0 && (
        <section className="space-y-2">
          <h3 className="text-sm font-medium">Por día (últimos 30 días)</h3>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Día</TableHead>
                <TableHead className="text-right">Respuestas</TableHead>
                <TableHead className="hidden text-right md:table-cell">Tokens</TableHead>
                <TableHead className="text-right">Costo</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {[...u.days].reverse().map((d) => (
                <TableRow key={d.day}>
                  <TableCell>{d.day}</TableCell>
                  <TableCell className="text-right">{number(d.replies)}</TableCell>
                  <TableCell className="hidden text-right md:table-cell">{number(d.tokens_in + d.tokens_out)}</TableCell>
                  <TableCell className="text-right">{usd(d.cost_micros)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </section>
      )}
    </div>
  )
}
