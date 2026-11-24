import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PlusIcon } from 'lucide-react'
import { useState, useSyncExternalStore } from 'react'
import { toast } from 'sonner'
import { ConfirmDialog, EmptyState, FieldError, NativeSelect, PageHeader, SecretOnce, formatDate, notifyError } from '@/components/common'
import { useClientOptions } from '@/components/client-scope-selector'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { UserAvatar } from '@/components/user-avatar'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ApiError, del, get, patch, post } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { getClientScope, subscribeClientScope } from '@/lib/client-scope'
import { ROLE_LABEL, type PendingInvite, type Role, type TeamMember } from '@/lib/types'

const CLIENT_ROLES: Role[] = ['client_admin', 'operator']
const AGENCY_ROLES: Role[] = ['agency_owner', 'agency_admin']

/** Equipo del cliente: el propio para su administrador; para la agencia, el del cliente elegido en el selector. */
export default function TeamPage() {
  const { isAgency } = useAuth()
  const scope = useSyncExternalStore(subscribeClientScope, getClientScope)
  const clientName = useClientOptions(isAgency && !!scope).data?.clients.find((c) => c.id === scope)?.name
  if (isAgency && !scope) {
    return (
      <>
        <PageHeader title="Equipo del cliente" description="Administradores y operadores de cada cliente." />
        <EmptyState>Elige un cliente en el selector «Estás viendo» para ver y gestionar su equipo.</EmptyState>
      </>
    )
  }

  return (
    <TeamView
      base="/panel/users"
      scope={scope ?? 'own'}
      roles={CLIENT_ROLES}
      canEdit
      title={clientName ? `Equipo de ${clientName}` : 'Equipo'}
      description="Los administradores alimentan el chatbot, ven contactos y consumo, y gestionan el equipo. Los operadores solo atienden la bandeja."
      roleHint="Administrador: conocimiento, conversaciones, contactos, consumo y equipo. Operador: solo la bandeja."
    />
  )
}

/** Equipo de la agencia: lo ven todos en la agencia; solo su dueño lo cambia. */
export function AgencyTeamPage() {
  const { isAgencyOwner } = useAuth()
  return (
    <TeamView
      base="/panel/agency/users"
      scope="agency"
      roles={AGENCY_ROLES}
      canEdit={isAgencyOwner}
      title="Equipo de la agencia"
      description="Quién en Pixel DC administra clientes, chatbots e integraciones. Solo el dueño de la agencia cambia este equipo."
      roleHint="Dueño: todo, incluido este equipo. Equipo de la agencia: clientes, chatbots, integraciones y configuración."
    />
  )
}

function TeamView({ base, scope, roles, canEdit, title, description, roleHint }: { base: string; scope: string; roles: Role[]; canEdit: boolean; title: string; description: string; roleHint: string }) {
  const { user } = useAuth()
  const client = useQueryClient()
  const [inviting, setInviting] = useState(false)
  const [link, setLink] = useState<string | null>(null)
  const [removing, setRemoving] = useState<TeamMember | null>(null)
  const [resetting, setResetting] = useState<TeamMember | null>(null)
  const [resetLink, setResetLink] = useState<{ email: string; url: string; twoFactorRemoved: boolean } | null>(null)

  const users = useQuery({ queryKey: ['team', base, scope], queryFn: () => get<{ users: TeamMember[] }>(`${base}/`) })
  const invites = useQuery({ queryKey: ['team-invites', base, scope], queryFn: () => get<{ invites: PendingInvite[] }>(`${base}/invites`) })

  const refresh = () => {
    client.invalidateQueries({ queryKey: ['team'] })
    client.invalidateQueries({ queryKey: ['team-invites'] })
  }

  const update = useMutation({
    mutationFn: ({ id, ...body }: { id: string; role?: Role; is_active?: boolean }) => patch(`${base}/${id}`, body),
    onSuccess: () => {
      refresh()
      toast.success('Cambios guardados. Se cerraron las sesiones abiertas de esa persona.')
    },
    onError: notifyError,
  })
  const remove = useMutation({
    mutationFn: (id: string) => del(`${base}/${id}`),
    onSuccess: () => {
      refresh()
      setRemoving(null)
    },
    onError: notifyError,
  })
  const revokeInvite = useMutation({
    mutationFn: (id: string) => del(`${base}/invites/${id}`),
    onSuccess: refresh,
    onError: notifyError,
  })

  const canManageTarget = (target: TeamMember) => canEdit && target.id !== user?.id

  return (
    <>
      <PageHeader
        title={title}
        description={description}
        actions={
          canEdit && (
            <Button onClick={() => setInviting(true)}>
              <PlusIcon /> Invitar
            </Button>
          )
        }
      />

      {users.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader className="max-md:hidden">
              <TableRow>
                <TableHead>Persona</TableHead>
                <TableHead>Rol</TableHead>
                <TableHead className="hidden md:table-cell">2FA</TableHead>
                <TableHead className="hidden md:table-cell">Último acceso</TableHead>
                <TableHead className="w-40" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {users.data?.users.map((m) => (
                <TableRow key={m.id} className={`max-md:flex max-md:flex-wrap max-md:items-center max-md:gap-x-3 max-md:gap-y-2 max-md:p-3 ${m.is_active ? '' : 'opacity-60'}`}>
                  <TableCell className="max-md:w-full max-md:p-0">
                    <div className="flex items-center gap-3">
                      <UserAvatar user={m} />
                      <div className="min-w-0">
                        <div className="truncate font-medium">{m.display_name?.trim() || m.email}</div>
                        {m.display_name?.trim() && <div className="truncate text-xs text-muted-foreground">{m.email}</div>}
                        {!m.is_active && <Badge variant="outline">Desactivado</Badge>}
                        {m.id === user?.id && <Badge variant="secondary">Tú</Badge>}
                      </div>
                    </div>
                  </TableCell>
                  <TableCell className="max-md:p-0">
                    {canManageTarget(m) ? (
                      <NativeSelect
                        aria-label={`Rol de ${m.email}`}
                        value={m.role}
                        disabled={update.isPending}
                        onChange={(e) => update.mutate({ id: m.id, role: e.target.value as Role })}
                      >
                        {roles.map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
                      </NativeSelect>
                    ) : (
                      ROLE_LABEL[m.role]
                    )}
                  </TableCell>
                  <TableCell className="hidden md:table-cell">{m.two_factor_enabled ? 'Activo' : '—'}</TableCell>
                  <TableCell className="hidden md:table-cell">{formatDate(m.last_login_at)}</TableCell>
                  <TableCell className="text-right max-md:w-full max-md:p-0 max-md:text-left max-md:whitespace-normal">
                    {canManageTarget(m) && (
                      <div className="flex flex-wrap justify-end gap-1 max-md:justify-start">
                        <Button variant="ghost" size="sm" disabled={update.isPending} onClick={() => update.mutate({ id: m.id, is_active: !m.is_active })}>
                          {m.is_active ? 'Desactivar' : 'Activar'}
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => setResetting(m)}>Restablecer acceso</Button>
                        <Button variant="ghost" size="sm" onClick={() => setRemoving(m)}>Eliminar</Button>
                      </div>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      {!!invites.data?.invites.length && (
        <section className="space-y-2">
          <h2 className="text-sm font-medium">Invitaciones pendientes</h2>
          <div className="rounded-lg border">
            <Table>
              <TableBody>
                {invites.data.invites.map((i) => (
                  <TableRow key={i.id}>
                    <TableCell>{i.email}</TableCell>
                    <TableCell>{ROLE_LABEL[i.role]}</TableCell>
                    <TableCell className="hidden sm:table-cell">Vence {formatDate(i.expires_at)}</TableCell>
                    <TableCell className="text-right">
                      {canEdit && <Button variant="ghost" size="sm" onClick={() => revokeInvite.mutate(i.id)}>Cancelar</Button>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </section>
      )}

      <InviteDialog
        base={base}
        hint={roleHint}
        open={inviting}
        onOpenChange={setInviting}
        roles={roles}
        onCreated={(token) => {
          refresh()
          setInviting(false)
          setLink(`${window.location.origin}/invitacion#token=${token}`)
        }}
      />

      <Dialog open={!!link} onOpenChange={(o) => !o && setLink(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Invitación creada</DialogTitle>
            <DialogDescription>Envía este enlace a la persona invitada. Vence en 48 horas y sirve una sola vez.</DialogDescription>
          </DialogHeader>
          {link && <SecretOnce label="Enlace de invitación" value={link} hint="No podrás volver a verlo; si se pierde, cancela la invitación y crea otra." />}
          <DialogFooter>
            <Button onClick={() => setLink(null)}>Listo</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <ResetDialog
        base={base}
        member={resetting}
        onClose={() => setResetting(null)}
        onCreated={(r) => {
          setResetting(null)
          setResetLink(r)
        }}
      />

      <Dialog open={!!resetLink} onOpenChange={(o) => !o && setResetLink(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Enlace de recuperación</DialogTitle>
            <DialogDescription>
              Para {resetLink?.email}. Envíaselo por un canal seguro; vence en 2 horas y sirve una sola vez.
              {resetLink?.twoFactorRemoved && ' Al usarlo también se quitará su verificación en dos pasos.'}
            </DialogDescription>
          </DialogHeader>
          {resetLink && <SecretOnce label="Enlace" value={resetLink.url} hint="No podrás volver a verlo; si se pierde, genera otro." />}
          <DialogFooter>
            <Button onClick={() => setResetLink(null)}>Listo</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Eliminar usuario"
        description={`${removing?.email} perderá el acceso y sus sesiones se cerrarán.`}
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing.id)}
      />
    </>
  )
}

function InviteDialog({ base, hint, open, onOpenChange, roles, onCreated }: { base: string; hint: string; open: boolean; onOpenChange: (o: boolean) => void; roles: Role[]; onCreated: (token: string) => void }) {
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<Role>(roles[roles.length - 1])
  const [error, setError] = useState<string | null>(null)

  const invite = useMutation({
    mutationFn: () => post<{ token: string }>(`${base}/invites`, { email, role }),
    onSuccess: (r) => {
      setEmail('')
      setError(null)
      onCreated(r.token)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo crear la invitación.'),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Invitar a una persona</DialogTitle>
          <DialogDescription>Recibirá un enlace para crear su contraseña.</DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            invite.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="inv-email">Correo</Label>
            <Input id="inv-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoFocus />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="inv-role">Rol</Label>
            <NativeSelect id="inv-role" value={role} onChange={(e) => setRole(e.target.value as Role)}>
              {roles.map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
            </NativeSelect>
            <p className="text-xs text-muted-foreground">{hint}</p>
          </div>
          <FieldError message={error ?? undefined} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>Cancelar</Button>
            <Button type="submit" disabled={invite.isPending || !email.includes('@')}>Crear invitación</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function ResetDialog({
  base,
  member,
  onClose,
  onCreated,
}: {
  base: string
  member: TeamMember | null
  onClose: () => void
  onCreated: (r: { email: string; url: string; twoFactorRemoved: boolean }) => void
}) {
  const [removeTwoFactor, setRemoveTwoFactor] = useState(false)
  const create = useMutation({
    mutationFn: () =>
      post<{ email: string; token: string; reset_two_factor: boolean }>(`${base}/${member!.id}/reset-link`, { reset_two_factor: removeTwoFactor }),
    onSuccess: (r) => {
      setRemoveTwoFactor(false)
      onCreated({ email: r.email, url: `${window.location.origin}/restablecer#token=${r.token}`, twoFactorRemoved: r.reset_two_factor })
    },
    onError: notifyError,
  })

  return (
    <Dialog open={!!member} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Restablecer acceso</DialogTitle>
          <DialogDescription>
            Se generará un enlace para que {member?.email} elija una contraseña nueva. Sus sesiones abiertas se cerrarán al usarlo.
          </DialogDescription>
        </DialogHeader>
        {member?.two_factor_enabled && (
          <label className="flex items-start gap-2 text-sm">
            <input type="checkbox" className="mt-1" checked={removeTwoFactor} onChange={(e) => setRemoveTwoFactor(e.target.checked)} />
            <span>
              También quitar su verificación en dos pasos
              <span className="block text-xs text-muted-foreground">Solo si perdió su teléfono y sus códigos de recuperación. Podrá volver a activarla.</span>
            </span>
          </label>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>Cancelar</Button>
          <Button onClick={() => create.mutate()} disabled={create.isPending}>Generar enlace</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
