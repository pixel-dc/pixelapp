import { BotIcon } from 'lucide-react'
import { Avatar, AvatarFallback, AvatarImage } from '@/components/ui/avatar'

/** Un color estable por persona (el mismo correo, el mismo color) para que el avatar sin foto no sea un círculo gris. */
function colorFor(seed: string): string {
  let h = 0
  for (let i = 0; i < seed.length; i++) h = (h * 31 + seed.charCodeAt(i)) % 360
  return `hsl(${h} 62% 42%)`
}

function initialsOf(name: string | null | undefined, email: string): string {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean)
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase()
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase()
  return email.split('@')[0].replace(/[^a-z0-9]/gi, '').slice(0, 2).toUpperCase() || '?'
}

interface Person {
  id: string
  email: string
  display_name?: string | null
  avatar_version?: number | null
}

/** Foto de la persona si la subió; si no, sus iniciales sobre un color propio. */
export function UserAvatar({ user, size = 'default', className }: { user: Person; size?: 'default' | 'sm' | 'lg'; className?: string }) {
  const label = user.display_name?.trim() || user.email
  return (
    <Avatar size={size} className={className}>
      {user.avatar_version ? <AvatarImage src={`/panel/avatars/${user.id}?v=${user.avatar_version}`} alt={label} /> : null}
      <AvatarFallback className="font-semibold text-white" style={{ background: colorFor(user.email) }} aria-label={label}>
        {initialsOf(user.display_name, user.email)}
      </AvatarFallback>
    </Avatar>
  )
}

/** Icono del chatbot si lo tiene; si no, el robot sobre el color del bot. */
export function BotAvatar({ bot, size = 'default', className }: { bot: { id: string; name: string; color?: string | null; icon_version?: number | null }; size?: 'default' | 'sm' | 'lg'; className?: string }) {
  return (
    <Avatar size={size} className={className}>
      {bot.icon_version ? <AvatarImage src={`/panel/bots/${bot.id}/icon?v=${bot.icon_version}`} alt={bot.name} /> : null}
      <AvatarFallback className="text-white" style={{ background: bot.color ?? 'var(--primary)' }} aria-label={bot.name}>
        <BotIcon className="size-1/2" aria-hidden />
      </AvatarFallback>
    </Avatar>
  )
}
