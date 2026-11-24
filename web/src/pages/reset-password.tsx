import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { Link, useNavigate } from 'react-router'
import { toast } from 'sonner'
import { z } from 'zod'
import { FieldError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ApiError, post } from '@/lib/api'

const schema = z
  .object({
    password: z.string().min(12, 'Usa al menos 12 caracteres.').max(128),
    confirm: z.string(),
  })
  .refine((v) => v.password === v.confirm, { path: ['confirm'], message: 'Las contraseñas no coinciden.' })
type Values = z.infer<typeof schema>

/** El token viaja en el fragmento (#token=…): no se envía al servidor ni queda en registros de acceso. */
function readToken() {
  return new URLSearchParams(window.location.hash.replace(/^#/, '')).get('token') ?? ''
}

export default function ResetPasswordPage() {
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)
  const token = readToken()
  const form = useForm<Values>({ resolver: zodResolver(schema) })

  const submit = form.handleSubmit(async (values) => {
    setError(null)
    try {
      await post('/panel/auth/reset-password', { token, password: values.password })
      toast.success('Contraseña actualizada. Inicia sesión con la nueva.')
      navigate('/login', { replace: true })
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'No se pudo cambiar la contraseña.')
    }
  })

  return (
    <main className="grid min-h-screen place-items-center p-4">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle>Nueva contraseña</CardTitle>
          <CardDescription>Elige una contraseña nueva para recuperar el acceso. Se cerrarán tus sesiones abiertas.</CardDescription>
        </CardHeader>
        <CardContent>
          {!token ? (
            <p className="text-sm text-destructive">El enlace no es válido.</p>
          ) : (
            <form onSubmit={submit} className="space-y-4" noValidate>
              <div className="space-y-1.5">
                <Label htmlFor="password">Contraseña nueva</Label>
                <Input id="password" type="password" autoComplete="new-password" autoFocus {...form.register('password')} />
                <FieldError message={form.formState.errors.password?.message} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="confirm">Repite la contraseña</Label>
                <Input id="confirm" type="password" autoComplete="new-password" {...form.register('confirm')} />
                <FieldError message={form.formState.errors.confirm?.message} />
              </div>
              <FieldError message={error ?? undefined} />
              <Button type="submit" className="w-full" disabled={form.formState.isSubmitting}>
                Cambiar contraseña
              </Button>
            </form>
          )}
          <p className="mt-4 text-center text-sm text-muted-foreground">
            <Link to="/login" className="underline-offset-4 hover:underline">Ir al inicio de sesión</Link>
          </p>
        </CardContent>
      </Card>
    </main>
  )
}
