import { zodResolver } from '@hookform/resolvers/zod'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { Navigate } from 'react-router'
import { z } from 'zod'
import { BrandMark } from '@/components/app-shell'
import { FieldError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ApiError, get, post } from '@/lib/api'
import { useAuth } from '@/lib/auth'

const credentials = z.object({
  email: z.email('Ingresa un correo válido.'),
  password: z.string().min(1, 'Ingresa tu contraseña.'),
})
type Credentials = z.infer<typeof credentials>

type LoginResponse = { user?: unknown; two_factor_required?: boolean; challenge?: string }

export default function LoginPage() {
  const { user, refresh } = useAuth()
  const [challenge, setChallenge] = useState<string | null>(null)
  const setup = useQuery({ queryKey: ['setup'], queryFn: () => get<{ pending: boolean }>('/panel/setup'), retry: false })

  if (user) return <Navigate to="/" replace />
  if (setup.data?.pending) return <Navigate to="/configurar" replace />

  return (
    <main className="grid min-h-screen place-items-center p-4">
      <div className="w-full max-w-sm space-y-6">
        <div className="flex flex-col items-center gap-1 text-xl font-semibold">
          <BrandMark full className="size-56" />
          <span className="sr-only">Pixelapp</span>
        </div>
        <Card>
          {challenge ? (
            <TwoFactorStep challenge={challenge} onDone={refresh} onBack={() => setChallenge(null)} />
          ) : (
            <PasswordStep onChallenge={setChallenge} onDone={refresh} />
          )}
        </Card>
      </div>
    </main>
  )
}

function PasswordStep({ onChallenge, onDone }: { onChallenge: (c: string) => void; onDone: () => Promise<unknown> }) {
  const [error, setError] = useState<string | null>(null)
  const form = useForm<Credentials>({ resolver: zodResolver(credentials) })

  const submit = form.handleSubmit(async (values) => {
    setError(null)
    try {
      const result = await post<LoginResponse>('/panel/auth/login', values)
      if (result.two_factor_required && result.challenge) onChallenge(result.challenge)
      else await onDone()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'No se pudo iniciar sesión.')
    }
  })

  return (
    <>
      <CardHeader>
        <CardTitle>Iniciar sesión</CardTitle>
        <CardDescription>Accede al panel de tu cuenta.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-4" noValidate>
          <div className="space-y-1.5">
            <Label htmlFor="email">Correo</Label>
            <Input id="email" type="email" autoComplete="username" autoFocus {...form.register('email')} />
            <FieldError message={form.formState.errors.email?.message} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="password">Contraseña</Label>
            <Input id="password" type="password" autoComplete="current-password" {...form.register('password')} />
            <FieldError message={form.formState.errors.password?.message} />
          </div>
          <FieldError message={error ?? undefined} />
          <Button type="submit" className="w-full" disabled={form.formState.isSubmitting}>
            Entrar
          </Button>
        </form>
      </CardContent>
    </>
  )
}

function TwoFactorStep({ challenge, onDone, onBack }: { challenge: string; onDone: () => Promise<unknown>; onBack: () => void }) {
  const [useRecovery, setUseRecovery] = useState(false)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await post('/panel/auth/login/2fa', useRecovery ? { challenge, recovery_code: code } : { challenge, code })
      await onDone()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'No se pudo verificar el código.')
      setBusy(false)
    }
  }

  return (
    <>
      <CardHeader>
        <CardTitle>Verificación en dos pasos</CardTitle>
        <CardDescription>
          {useRecovery ? 'Ingresa uno de tus códigos de recuperación.' : 'Ingresa el código de 6 dígitos de tu app autenticadora.'}
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-4">
          <div className="space-y-1.5">
            <Label htmlFor="code">{useRecovery ? 'Código de recuperación' : 'Código'}</Label>
            <Input
              id="code"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              inputMode={useRecovery ? 'text' : 'numeric'}
              autoComplete="one-time-code"
              autoFocus
              maxLength={useRecovery ? 16 : 7}
            />
          </div>
          <FieldError message={error ?? undefined} />
          <Button type="submit" className="w-full" disabled={busy || code.trim().length < 6}>
            Verificar
          </Button>
          <div className="flex justify-between text-sm">
            <button type="button" className="text-muted-foreground underline-offset-4 hover:underline" onClick={() => { setUseRecovery(!useRecovery); setCode('') }}>
              {useRecovery ? 'Usar app autenticadora' : 'Usar código de recuperación'}
            </button>
            <button type="button" className="text-muted-foreground underline-offset-4 hover:underline" onClick={onBack}>
              Volver
            </button>
          </div>
        </form>
      </CardContent>
    </>
  )
}
