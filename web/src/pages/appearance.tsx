import { useMutation } from '@tanstack/react-query'
import { CheckIcon, ImagePlusIcon, MonitorIcon, MoonIcon, RotateCcwIcon, SunIcon, Trash2Icon } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ColorPicker } from '@/components/color-picker'
import { PageHeader, notifyError } from '@/components/common'
import { UserAvatar } from '@/components/user-avatar'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { api, del, put } from '@/lib/api'
import { type Appearance, BACKGROUNDS, RADII, defaultAccent, TEXT_SIZES, applyAppearance, cacheAppearance, sanitize, setEditingAppearance } from '@/lib/appearance'
import { useAuth } from '@/lib/auth'
import { resizeToSquare } from '@/lib/image'
import { useTheme } from '@/lib/theme'

/** Cada opción es un botón de radio con su propia vista previa: se ve lo que se elige. */
function Choice<T extends string>({ label, value, onChange, options }: { label: string; value: T; onChange: (v: T) => void; options: { value: T; label: string; preview: ReactNode }[] }) {
  return (
    <div role="radiogroup" aria-label={label} className="grid grid-cols-2 gap-2 sm:grid-cols-4">
      {options.map((o) => {
        const selected = value === o.value
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(o.value)}
            className={`group relative flex flex-col items-stretch gap-2 rounded-xl border p-2 text-left text-sm transition hover:border-primary/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${selected ? 'border-primary ring-2 ring-primary/30' : ''}`}
          >
            <span className="block overflow-hidden rounded-lg">{o.preview}</span>
            <span className="flex items-center justify-between px-1 font-medium">
              {o.label}
              {selected && <CheckIcon className="size-4 text-primary" aria-hidden />}
            </span>
          </button>
        )
      })}
    </div>
  )
}

const BG_PREVIEW: Record<string, string> = {
  plain: 'var(--background)',
  soft: 'linear-gradient(180deg, color-mix(in oklab, var(--primary) 14%, var(--background)), var(--background))',
  gradient: 'radial-gradient(120px 60px at 100% 0%, color-mix(in oklab, var(--primary) 45%, transparent), transparent 70%), radial-gradient(100px 60px at 0% 100%, color-mix(in oklab, var(--primary) 30%, transparent), transparent 70%), var(--background)',
  dots: 'radial-gradient(color-mix(in oklab, var(--primary) 40%, transparent) 1px, transparent 1px) 0 0 / 10px 10px, var(--background)',
}

/** Perfil y apariencia del panel de cada usuario: foto, nombre, color de acento, fondo, esquinas y tamaño del texto. */
export default function AppearancePage() {
  const { user, refresh } = useAuth()
  const { setTheme, theme } = useTheme()
  const saved = sanitize(user?.preferences)
  const [draft, setDraft] = useState<Appearance>(() => ({ ...saved, theme: saved.theme ?? theme }))
  const [name, setName] = useState(user?.display_name ?? '')
  const fileInput = useRef<HTMLInputElement>(null)
  const dirty = JSON.stringify(sanitize(draft)) !== JSON.stringify(sanitize({ ...saved, theme: saved.theme ?? theme }))

  // La vista previa es el panel mismo: cada cambio se aplica al instante y, si no se guarda, al salir se vuelve a lo guardado.
  useEffect(() => {
    setEditingAppearance(true)
    return () => {
      setEditingAppearance(false)
      applyAppearance(sanitize(user?.preferences))
      setTheme(sanitize(user?.preferences).theme ?? theme)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  useEffect(() => {
    applyAppearance(draft)
    if (draft.theme) setTheme(draft.theme)
  }, [draft, setTheme])

  const set = <K extends keyof Appearance>(key: K, value: Appearance[K]) => setDraft((d) => ({ ...d, [key]: value || undefined }))

  const savePrefs = useMutation({
    mutationFn: () => put('/panel/me/preferences', { ...sanitize(draft) }),
    onSuccess: async () => {
      cacheAppearance(draft)
      await refresh()
      toast.success('Apariencia guardada. Te acompaña en cualquier dispositivo.')
    },
    onError: notifyError,
  })
  const saveName = useMutation({
    mutationFn: () => put('/panel/me/profile', { display_name: name }),
    onSuccess: async () => {
      await refresh()
      toast.success('Nombre guardado.')
    },
    onError: notifyError,
  })
  const upload = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData()
      form.append('file', await resizeToSquare(file))
      return api('PUT', '/panel/me/avatar', form)
    },
    onSuccess: async () => {
      await refresh()
      toast.success('Foto actualizada.')
    },
    onError: notifyError,
  })
  const removeAvatar = useMutation({
    mutationFn: () => del('/panel/me/avatar'),
    onSuccess: async () => {
      await refresh()
      toast.success('Foto quitada.')
    },
    onError: notifyError,
  })

  if (!user) return null
  return (
    <>
      <PageHeader title="Mi perfil y apariencia" description="Elige cómo se ve el panel para ti. Se guarda en tu cuenta y no cambia lo que ven los demás." />

      <div className="grid gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
        <Card>
          <CardHeader>
            <CardTitle>Perfil</CardTitle>
            <CardDescription>Tu foto y tu nombre aparecen en el menú y en el equipo.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-5">
            <div className="flex items-center gap-4">
              <UserAvatar user={user} size="lg" className="size-20! text-2xl" />
              <div className="flex flex-wrap gap-2">
                <input
                  ref={fileInput}
                  type="file"
                  accept="image/png,image/jpeg,image/webp"
                  className="sr-only"
                  aria-label="Elegir foto de perfil"
                  onChange={(e) => {
                    const file = e.target.files?.[0]
                    e.target.value = ''
                    if (file) upload.mutate(file)
                  }}
                />
                <Button type="button" variant="outline" size="sm" disabled={upload.isPending} onClick={() => fileInput.current?.click()}>
                  <ImagePlusIcon /> {user.avatar_version ? 'Cambiar foto' : 'Subir foto'}
                </Button>
                {user.avatar_version ? (
                  <Button type="button" variant="ghost" size="sm" disabled={removeAvatar.isPending} onClick={() => removeAvatar.mutate()}>
                    <Trash2Icon /> Quitar
                  </Button>
                ) : null}
              </div>
            </div>
            <p className="text-xs text-muted-foreground">PNG, JPG o WebP. Se recorta en cuadrado y se reduce sola.</p>
            <form
              className="space-y-1.5"
              onSubmit={(e) => {
                e.preventDefault()
                saveName.mutate()
              }}
            >
              <Label htmlFor="profile-name">Nombre</Label>
              <div className="flex gap-2">
                <Input id="profile-name" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} placeholder={user.email.split('@')[0]} />
                <Button type="submit" disabled={saveName.isPending || name.trim() === (user.display_name ?? '')}>
                  Guardar
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Apariencia</CardTitle>
            <CardDescription>Los cambios se ven al instante. Guárdalos para conservarlos.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-6">
            <section className="space-y-2">
              <Label id="accent-label">Color</Label>
              <ColorPicker id="accent" label="Color del panel" value={draft.accent ?? ''} onChange={(v) => set('accent', v)} fallback={defaultAccent()} />
              <p className="text-xs text-muted-foreground">Botones, enlaces, selección y gráficos usan este color.</p>
            </section>

            <section className="space-y-2">
              <Label>Tema</Label>
              <Choice
                label="Tema"
                value={draft.theme ?? 'system'}
                onChange={(v) => set('theme', v)}
                options={[
                  { value: 'light', label: 'Claro', preview: <div className="grid h-14 place-items-center bg-white text-slate-700"><SunIcon className="size-5" /></div> },
                  { value: 'dark', label: 'Oscuro', preview: <div className="grid h-14 place-items-center bg-slate-900 text-slate-100"><MoonIcon className="size-5" /></div> },
                  { value: 'system', label: 'Del sistema', preview: <div className="grid h-14 place-items-center bg-gradient-to-r from-white to-slate-900 text-slate-500"><MonitorIcon className="size-5" /></div> },
                ]}
              />
            </section>

            <section className="space-y-2">
              <Label>Fondo</Label>
              <Choice
                label="Fondo"
                value={draft.background ?? 'soft'}
                onChange={(v) => set('background', v)}
                options={BACKGROUNDS.map((b) => ({ ...b, preview: <div className="h-14 border" style={{ background: BG_PREVIEW[b.value] }} /> }))}
              />
            </section>

            <section className="space-y-2">
              <Label>Esquinas</Label>
              <Choice
                label="Esquinas"
                value={draft.radius ?? 'md'}
                onChange={(v) => set('radius', v)}
                options={RADII.map((r) => ({ value: r.value, label: r.label, preview: <div className="grid h-14 place-items-center bg-muted"><div className="h-8 w-16 border-2 border-primary bg-background" style={{ borderRadius: r.css }} /></div> }))}
              />
            </section>

            <section className="space-y-2">
              <Label>Tamaño del texto</Label>
              <Choice
                label="Tamaño del texto"
                value={draft.text_size ?? 'md'}
                onChange={(v) => set('text_size', v)}
                options={TEXT_SIZES.map((t) => ({ value: t.value, label: t.label, preview: <div className="grid h-14 place-items-center bg-muted font-semibold" style={{ fontSize: t.css }}>Aa</div> }))}
              />
            </section>

            <div className="flex flex-wrap items-center gap-2 border-t pt-4">
              <Button onClick={() => savePrefs.mutate()} disabled={!dirty || savePrefs.isPending}>
                Guardar apariencia
              </Button>
              <Button variant="ghost" onClick={() => setDraft({ theme: 'system' })}>
                <RotateCcwIcon /> Restablecer
              </Button>
              {dirty && <span className="text-xs text-muted-foreground">Tienes cambios sin guardar.</span>}
            </div>
          </CardContent>
        </Card>
      </div>
    </>
  )
}
