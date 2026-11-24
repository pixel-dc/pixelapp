import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Navigate, useNavigate } from 'react-router'
import { BrandMark } from '@/components/app-shell'
import { FieldError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ApiError, get, post } from '@/lib/api'

/** Primer arranque: mientras no hay ningún usuario, se crea el dueño de la agencia. Después esta pantalla deja de existir. */
export default function SetupPage() {
  const navigate = useNavigate()
  const status = useQuery({ queryKey: ['setup'], queryFn: () => get<{ pending: boolean }>('/panel/setup') })
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  if (status.data && !status.data.pending) return <Navigate to="/login" replace />

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (password !== confirm) {
      setError('Las contraseñas no coinciden.')
      return
    }

    setSaving(true)
    setError(null)
    try {
      await post('/panel/setup', { email, password, display_name: name })
      navigate('/login', { replace: true, state: { created: email } })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'No se pudo completar la configuración.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <main className="grid min-h-screen place-items-center bg-[#111118] p-4">
      <div className="w-full max-w-md space-y-6">
        <div className="flex items-center justify-center gap-3 text-xl font-semibold text-white">
          <BrandMark className="size-12" /> Pixela
        </div>
        <Card>
          <CardHeader>
            <CardTitle>Bienvenido a Pixela</CardTitle>
            <CardDescription>Crea la cuenta del dueño de la agencia. Desde ella invitarás al resto del equipo y a tus clientes.</CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-4" onSubmit={submit}>
              <div className="space-y-1.5">
                <Label htmlFor="setup-name">Tu nombre</Label>
                <Input id="setup-name" value={name} onChange={(e) => setName(e.target.value)} autoComplete="name" />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="setup-email">Correo</Label>
                <Input id="setup-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="setup-password">Contraseña</Label>
                <Input id="setup-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" required />
                <p className="text-xs text-muted-foreground">Al menos 12 caracteres. Una frase larga es más fácil de recordar y más segura.</p>
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="setup-confirm">Repite la contraseña</Label>
                <Input id="setup-confirm" type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" required />
              </div>
              <FieldError message={error ?? undefined} />
              <Button type="submit" className="w-full" disabled={saving || status.isLoading}>
                Crear cuenta de la agencia
              </Button>
            </form>
          </CardContent>
        </Card>
      </div>
    </main>
  )
}
