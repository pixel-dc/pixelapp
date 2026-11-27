import { useMutation } from '@tanstack/react-query'
import { QRCodeSVG } from 'qrcode.react'
import { useState } from 'react'
import { toast } from 'sonner'
import { FieldError, PageHeader, SecretOnce } from '@/components/common'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { NotificationsCard } from '@/components/notifications'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ApiError, post } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { ROLE_LABEL } from '@/lib/types'

export default function AccountPage() {
  const { user } = useAuth()
  if (!user) return null
  return (
    <>
      <PageHeader title="Mi cuenta" description={`${user.email} · ${ROLE_LABEL[user.role]}`} />
      <PasswordCard />
      <NotificationsCard />
      <TwoFactorCard />
    </>
  )
}

function PasswordCard() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)

  const change = useMutation({
    mutationFn: () => post('/panel/me/password', { current_password: current, new_password: next }),
    onSuccess: () => {
      toast.success('Contraseña actualizada. Se cerraron tus otras sesiones.')
      setCurrent('')
      setNext('')
      setConfirm('')
      setError(null)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo cambiar la contraseña.'),
  })

  const mismatch = confirm.length > 0 && confirm !== next
  return (
    <Card>
      <CardHeader>
        <CardTitle>Contraseña</CardTitle>
        <CardDescription>Mínimo 12 caracteres. Una frase larga es mejor que una contraseña corta con símbolos.</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="max-w-sm space-y-4"
          onSubmit={(e) => {
            e.preventDefault()
            if (!mismatch) change.mutate()
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="cur">Contraseña actual</Label>
            <Input id="cur" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="new">Nueva contraseña</Label>
            <Input id="new" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="conf">Repite la nueva contraseña</Label>
            <Input id="conf" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
            <FieldError message={mismatch ? 'Las contraseñas no coinciden.' : undefined} />
          </div>
          <FieldError message={error ?? undefined} />
          <Button type="submit" disabled={change.isPending || !current || next.length < 12 || mismatch || !confirm}>Cambiar contraseña</Button>
        </form>
      </CardContent>
    </Card>
  )
}

function TwoFactorCard() {
  const { user, refresh } = useAuth()
  const [setup, setSetup] = useState<{ secret: string; otpauth_uri: string } | null>(null)
  const [recovery, setRecovery] = useState<string[] | null>(null)
  const [code, setCode] = useState('')
  const [disabling, setDisabling] = useState(false)
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const begin = useMutation({
    mutationFn: () => post<{ secret: string; otpauth_uri: string }>('/panel/me/2fa/setup'),
    onSuccess: (r) => {
      setSetup(r)
      setError(null)
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo iniciar la configuración.'),
  })
  const enable = useMutation({
    mutationFn: () => post<{ recovery_codes: string[] }>('/panel/me/2fa/enable', { code }),
    onSuccess: async (r) => {
      setRecovery(r.recovery_codes)
      setSetup(null)
      setCode('')
      setError(null)
      await refresh()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo activar.'),
  })
  const disable = useMutation({
    mutationFn: () => post('/panel/me/2fa/disable', { password, code }),
    onSuccess: async () => {
      toast.success('Verificación en dos pasos desactivada.')
      setDisabling(false)
      setPassword('')
      setCode('')
      setError(null)
      await refresh()
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo desactivar.'),
  })

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Verificación en dos pasos {user?.two_factor_enabled ? <Badge>Activa</Badge> : <Badge variant="outline">Desactivada</Badge>}
        </CardTitle>
        <CardDescription>Opcional, pero muy recomendable: además de la contraseña pedimos un código de tu app autenticadora.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {recovery && (
          <Alert>
            <AlertTitle>Guarda tus códigos de recuperación</AlertTitle>
            <AlertDescription className="space-y-3">
              <p>Cada uno sirve una sola vez si pierdes el acceso a tu app. No volverán a mostrarse.</p>
              <div className="grid grid-cols-2 gap-2 font-mono text-sm sm:grid-cols-5">
                {recovery.map((c) => <code key={c} className="rounded border bg-muted px-2 py-1 select-all">{c}</code>)}
              </div>
              <Button size="sm" onClick={() => setRecovery(null)}>Ya los guardé</Button>
            </AlertDescription>
          </Alert>
        )}

        {!user?.two_factor_enabled && !setup && !recovery && (
          <Button onClick={() => begin.mutate()} disabled={begin.isPending}>Configurar</Button>
        )}

        {setup && (
          <div className="grid gap-4 sm:grid-cols-[auto_1fr]">
            <div className="w-fit rounded-lg border bg-white p-3">
              <QRCodeSVG value={setup.otpauth_uri} size={160} />
            </div>
            <div className="space-y-3">
              <p className="text-sm">Escanea el código con Google Authenticator, Authy o 1Password. Si no puedes escanear, ingresa la clave a mano.</p>
              <SecretOnce label="Clave" value={setup.secret} hint="Solo para configurar tu app." />
              <div className="space-y-1.5">
                <Label htmlFor="totp">Código de 6 dígitos</Label>
                <Input id="totp" inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code} onChange={(e) => setCode(e.target.value)} className="max-w-40" />
              </div>
              <div className="flex gap-2">
                <Button onClick={() => enable.mutate()} disabled={enable.isPending || code.trim().length < 6}>Activar</Button>
                <Button variant="outline" onClick={() => { setSetup(null); setCode('') }}>Cancelar</Button>
              </div>
            </div>
          </div>
        )}

        {user?.two_factor_enabled && !disabling && !recovery && (
          <Button variant="destructive" onClick={() => setDisabling(true)}>Desactivar</Button>
        )}

        {disabling && (
          <form
            className="max-w-sm space-y-3"
            onSubmit={(e) => {
              e.preventDefault()
              disable.mutate()
            }}
          >
            <p className="text-sm text-muted-foreground">Para desactivarla confirma tu contraseña y un código actual de tu app.</p>
            <div className="space-y-1.5">
              <Label htmlFor="dis-pass">Contraseña</Label>
              <Input id="dis-pass" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="dis-code">Código</Label>
              <Input id="dis-code" inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code} onChange={(e) => setCode(e.target.value)} />
            </div>
            <div className="flex gap-2">
              <Button type="submit" variant="destructive" disabled={disable.isPending || !password || code.trim().length < 6}>Desactivar</Button>
              <Button type="button" variant="outline" onClick={() => { setDisabling(false); setPassword(''); setCode('') }}>Cancelar</Button>
            </div>
          </form>
        )}

        <FieldError message={error ?? undefined} />
      </CardContent>
    </Card>
  )
}
