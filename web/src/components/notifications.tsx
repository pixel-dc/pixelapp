import { BellIcon, BellOffIcon, XIcon } from 'lucide-react'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { notifyError } from '@/components/common'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useBranding } from '@/lib/branding'
import { type PushState, disablePush, enablePush, pushState, testPush } from '@/lib/push'

function usePushState() {
  const [state, setState] = useState<PushState | null>(null)
  useEffect(() => {
    let alive = true
    pushState()
      .then((s) => alive && setState(s))
      .catch(() => alive && setState('unsupported'))
    return () => {
      alive = false
    }
  }, [])
  return [state, setState] as const
}

const HINT: Record<Exclude<PushState, 'on' | 'off'>, (name: string) => string> = {
  unsupported: () => 'Este navegador no admite notificaciones. Prueba con Chrome, Edge, Firefox o Safari actualizados.',
  'needs-install': (name) =>
    `En iPhone las notificaciones llegan solo a la app instalada: toca Compartir y luego «Añadir a pantalla de inicio», abre ${name} desde ese ícono y actívalas allí.`,
  denied: () => 'Bloqueaste las notificaciones de este sitio. Permítelas en los ajustes del navegador (o del teléfono) y vuelve aquí.',
}

/** Tarjeta de «Mi cuenta»: activar, probar o desactivar las notificaciones en este dispositivo. */
export function NotificationsCard() {
  const [state, setState] = usePushState()
  const [busy, setBusy] = useState(false)
  const { name } = useBranding()

  const run = async (action: () => Promise<PushState | void>, done?: string) => {
    setBusy(true)
    try {
      const next = await action()
      if (next) setState(next)
      if (done && (!next || next === 'on' || next === 'off')) toast.success(done)
    } catch (e) {
      notifyError(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <BellIcon className="size-4" aria-hidden /> Notificaciones en este dispositivo
        </CardTitle>
        <CardDescription>
          Te avisamos cuando empieza una conversación o cuando una necesita a una persona, aunque {name} esté cerrado. Se activan en cada teléfono o
          computador por separado.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {state === null ? (
          <p className="text-sm text-muted-foreground">Revisando…</p>
        ) : state === 'on' ? (
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" disabled={busy} onClick={() => run(async () => testPush(), 'Notificación de prueba enviada.')}>
              Enviar una prueba
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => run(disablePush, 'Notificaciones desactivadas en este dispositivo.')}>
              <BellOffIcon /> Desactivar
            </Button>
          </div>
        ) : state === 'off' ? (
          <Button disabled={busy} onClick={() => run(enablePush, 'Notificaciones activadas.')}>
            <BellIcon /> Activar notificaciones
          </Button>
        ) : (
          <p className="text-sm text-muted-foreground">{HINT[state](name)}</p>
        )}
        {state === 'on' && <p className="text-sm text-muted-foreground">Activas en este dispositivo.</p>}
      </CardContent>
    </Card>
  )
}

const DISMISS_KEY = 'px:push-banner-dismissed'

/** Aviso en la bandeja mientras este dispositivo no tenga las notificaciones activas. Se puede cerrar. */
export function NotificationsBanner() {
  const [state, setState] = usePushState()
  const [hidden, setHidden] = useState(() => {
    try {
      return localStorage.getItem(DISMISS_KEY) === '1'
    } catch {
      return false
    }
  })
  const [busy, setBusy] = useState(false)
  const { name } = useBranding()

  if (hidden || state === null || state === 'on' || state === 'unsupported') return null

  const dismiss = () => {
    setHidden(true)
    try {
      localStorage.setItem(DISMISS_KEY, '1')
    } catch {
      // Sin almacenamiento: vuelve a salir la próxima vez.
    }
  }

  const enable = async () => {
    setBusy(true)
    try {
      const next = await enablePush()
      setState(next)
      if (next === 'on') toast.success('Notificaciones activadas.')
    } catch (e) {
      notifyError(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div role="status" className="flex items-start gap-3 rounded-xl border bg-card p-3 text-sm shadow-sm">
      <BellIcon className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden />
      <div className="min-w-0 flex-1 space-y-2">
        <p>{state === 'off' ? 'Activa las notificaciones para enterarte cuando alguien escriba, aunque tengas la app cerrada.' : HINT[state](name)}</p>
        {state === 'off' && (
          <Button size="sm" disabled={busy} onClick={enable}>
            Activar notificaciones
          </Button>
        )}
      </div>
      <Button variant="ghost" size="icon" className="-m-1 size-7" onClick={dismiss} aria-label="Cerrar aviso">
        <XIcon />
      </Button>
    </div>
  )
}
