import { CheckIcon, CopyIcon } from 'lucide-react'
import { useState, type ReactNode, type SelectHTMLAttributes } from 'react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { ApiError } from '@/lib/api'
import { cn } from 'cn'

export function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
      <div>
        <h1 className="text-xl font-semibold tracking-tight sm:text-2xl">{title}</h1>
        {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 gap-2">{actions}</div>}
    </div>
  )
}

export function errorMessage(error: unknown): string {
  return error instanceof ApiError ? error.message : 'Ocurrió un error inesperado.'
}

export function notifyError(error: unknown) {
  toast.error(errorMessage(error))
}

export function FieldError({ message }: { message?: string }) {
  return message ? <p className="text-sm text-destructive" role="alert">{message}</p> : null
}

export function formatDate(value: string | null | undefined) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('es-CO', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
}

const STATUS_STYLES: Record<string, { label: string; variant: 'default' | 'secondary' | 'destructive' | 'outline' }> = {
  verified: { label: 'Verificado', variant: 'default' },
  partial: { label: 'Parcial', variant: 'secondary' },
  pending: { label: 'Pendiente', variant: 'outline' },
  suspended: { label: 'Suspendido', variant: 'destructive' },
}

export function StatusBadge({ status }: { status: string }) {
  const style = STATUS_STYLES[status.toLowerCase()] ?? { label: status, variant: 'outline' as const }
  return <Badge variant={style.variant}>{style.label}</Badge>
}

/** Botón que copia `value` al portapapeles y muestra una marca durante 2 s. */
export function CopyButton({ value, label = 'Copiar', size = 'icon' }: { value: string; label?: string; size?: 'icon' | 'icon-sm' }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      toast.error('No se pudo copiar. Selecciona el texto y cópialo manualmente.')
    }
  }
  return (
    <Button type="button" variant="outline" size={size} onClick={copy} aria-label={label}>
      {copied ? <CheckIcon /> : <CopyIcon />}
    </Button>
  )
}

/** Valor sensible que se muestra una sola vez (API key, secreto de webhook, enlace de invitación). */
export function SecretOnce({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="space-y-2">
      <p className="text-sm font-medium">{label}</p>
      <div className="flex items-start gap-2">
        <code className="min-w-0 flex-1 rounded-md border bg-muted px-3 py-2 text-xs break-all select-all">{value}</code>
        <CopyButton value={value} />
      </div>
      <p className="text-xs text-muted-foreground">{hint}</p>
    </div>
  )
}

export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel,
  pending,
  onConfirm,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description: string
  confirmLabel: string
  pending?: boolean
  onConfirm: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancelar
          </Button>
          <Button variant="destructive" disabled={pending} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export function NativeSelect({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      className={cn(
        'h-8 w-full rounded-lg border border-input bg-transparent px-2.5 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:opacity-50 dark:bg-input/30',
        className,
      )}
      {...props}
    />
  )
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <div className="rounded-lg border border-dashed p-8 text-center text-sm text-muted-foreground">{children}</div>
}
