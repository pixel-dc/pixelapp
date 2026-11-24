import { useQuery } from '@tanstack/react-query'
import { BotIcon, BriefcaseIcon, MessagesSquareIcon } from 'lucide-react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/common'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { get } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import type { BotList, Client } from '@/lib/types'

/** Resumen: clientes, chatbots y conversaciones del mes, más lo que espera a una persona. */
export default function DashboardPage() {
  const { user, isAgency } = useAuth()
  const clients = useQuery({ queryKey: ['clients'], queryFn: () => get<{ clients: Client[] }>('/panel/clients') })
  const bots = useQuery({ queryKey: ['bots', 'all'], queryFn: () => get<BotList>('/panel/bots/') })

  const list = clients.data?.clients ?? []
  const conversations = list.reduce((sum, c) => sum + c.conversations_this_month, 0)
  const attention = bots.data?.needing_attention ?? 0

  return (
    <>
      <PageHeader
        title={`Hola${user?.display_name ? `, ${user.display_name.split(' ')[0]}` : ''}`}
        description={isAgency ? 'Así va Pixela este mes.' : `Así van los chatbots de ${user?.client_name ?? 'tu negocio'} este mes.`}
      />
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {isAgency && (
          <Stat icon={BriefcaseIcon} label="Clientes" value={list.length} to="/clientes" hint={`${list.filter((c) => c.status === 'active').length} activos`} />
        )}
        <Stat icon={BotIcon} label="Chatbots" value={bots.data?.bots.length ?? 0} to="/chatbots" hint={`${bots.data?.bots.filter((b) => b.is_active).length ?? 0} activos`} />
        <Stat icon={MessagesSquareIcon} label="Conversaciones del mes" value={conversations} to="/chatbots" hint="Web, WhatsApp, Messenger e Instagram" />
        <Stat icon={MessagesSquareIcon} label="Esperan a una persona" value={attention} to="/bandeja" hint={attention > 0 ? 'Revisa la bandeja' : 'Nada pendiente'} highlight={attention > 0} />
      </div>
      {isAgency && list.length === 0 && !clients.isLoading && (
        <Card>
          <CardHeader>
            <CardTitle>Empieza por tu primer cliente</CardTitle>
            <CardDescription>
              Crea el cliente, un asset (una marca o sede) y dentro sus chatbots. Cada bot lleva su propia llave de Claude y su propia app de Meta.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Link to="/clientes" className="text-sm font-medium text-primary underline-offset-4 hover:underline">
              Ir a Clientes
            </Link>
          </CardContent>
        </Card>
      )}
    </>
  )
}

function Stat({ icon: Icon, label, value, hint, to, highlight }: { icon: typeof BotIcon; label: string; value: number; hint: string; to: string; highlight?: boolean }) {
  return (
    <Link to={to} className="rounded-xl outline-none focus-visible:ring-2 focus-visible:ring-ring">
      <Card className={highlight ? 'ring-2 ring-primary' : undefined}>
        <CardHeader className="flex flex-row items-center justify-between gap-2">
          <CardDescription>{label}</CardDescription>
          <Icon className="size-4 text-primary" aria-hidden />
        </CardHeader>
        <CardContent>
          <div className="text-3xl font-semibold tabular-nums">{value.toLocaleString('es-CO')}</div>
          <p className="text-xs text-muted-foreground">{hint}</p>
        </CardContent>
      </Card>
    </Link>
  )
}
