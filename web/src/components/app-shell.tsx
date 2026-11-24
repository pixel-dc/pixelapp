import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { BotIcon, BriefcaseIcon, ChevronsUpDownIcon, LayoutDashboardIcon, LogOutIcon, MessagesSquareIcon, MoonIcon, PaletteIcon, SettingsIcon, ShieldCheckIcon, SunIcon, UsersIcon, UserCogIcon } from 'lucide-react'
import { NavLink, Navigate, Outlet, useLocation, useNavigate } from 'react-router'
import { notifyError } from '@/components/common'
import { ClientScopeBanner, ClientScopeSelector } from '@/components/client-scope-selector'
import { UserAvatar } from '@/components/user-avatar'
import { DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarTrigger,
} from '@/components/ui/sidebar'
import { Skeleton } from '@/components/ui/skeleton'
import { get, post } from '@/lib/api'
import { applyAppearance, cacheAppearance, isEditingAppearance, sanitize } from '@/lib/appearance'
import { useAuth } from '@/lib/auth'
import { setClientScope } from '@/lib/client-scope'
import { useTheme } from '@/lib/theme'
import { type BotList, ROLE_LABEL } from '@/lib/types'

type Audience = 'all' | 'manage' | 'agency'

interface NavItem {
  to: string
  label: string
  icon: typeof BotIcon
  end?: boolean
  audience: Audience
  /** El operador también lo ve. */
  operator?: boolean
}

const NAV: NavItem[] = [
  { to: '/', label: 'Resumen', icon: LayoutDashboardIcon, end: true, audience: 'manage' },
  { to: '/clientes', label: 'Clientes', icon: BriefcaseIcon, audience: 'agency' },
  { to: '/chatbots', label: 'Chatbots', icon: BotIcon, audience: 'manage' },
  { to: '/bandeja', label: 'Bandeja', icon: MessagesSquareIcon, audience: 'all', operator: true },
  { to: '/equipo', label: 'Equipo del cliente', icon: UsersIcon, audience: 'manage' },
  { to: '/agencia/equipo', label: 'Equipo de la agencia', icon: UserCogIcon, audience: 'agency' },
  { to: '/agencia/configuracion', label: 'Configuración', icon: SettingsIcon, audience: 'agency' },
  { to: '/cuenta', label: 'Mi cuenta', icon: ShieldCheckIcon, audience: 'all', operator: true },
]

/** Cuántas conversaciones de WhatsApp, Messenger o Instagram esperan a una persona. Si falla, simplemente no se muestra. */
function AttentionBadge() {
  const { data } = useQuery({
    queryKey: ['bots-attention'],
    queryFn: () => get<BotList>('/panel/bots/'),
    refetchInterval: 60_000,
    staleTime: 30_000,
    retry: false,
  })
  const count = data?.needing_attention ?? 0
  if (count <= 0) return null
  return (
    <span className="ml-auto rounded-full bg-destructive px-1.5 text-xs font-medium text-white tabular-nums" aria-label={`${count} conversaciones esperan atención`}>
      {count}
    </span>
  )
}

/**
 * Logo de Pixelapp (el robot de Pixel DC). `full` = el logo completo con «PIXEL@» (entrada y primer arranque); si no, solo la cabeza
 * del robot, que se distingue a tamaño de ícono (menú). Si la imagen no carga, el distintivo «DC» con los colores de Pixel DC.
 */
export function BrandMark({ className = 'size-9', full = false }: { className?: string; full?: boolean }) {
  const [failed, setFailed] = useState(false)
  if (!failed) {
    return <img src={full ? '/pixela-logo.png' : '/pixela-icon.png'} alt={full ? 'Pixelapp' : ''} className={`${className} object-contain`} onError={() => setFailed(true)} />
  }
  return (
    <span className={`${className} grid place-items-center rounded-lg bg-[#1e1e2f] text-sm font-bold text-primary ring-1 ring-primary/60`} aria-hidden>
      DC
    </span>
  )
}

export function AppShell() {
  const { user, loading, isAgency, isOperator, canManage } = useAuth()
  const client = useQueryClient()
  const { theme, setTheme } = useTheme()
  const location = useLocation()
  const navigate = useNavigate()

  // La apariencia elegida en el servidor manda: se aplica al entrar desde cualquier dispositivo y se deja copia para no parpadear la próxima vez.
  const preferences = user?.preferences ? JSON.stringify(user.preferences) : ''
  useEffect(() => {
    if (!user || isEditingAppearance()) return
    const appearance = sanitize(user.preferences)
    applyAppearance(appearance)
    cacheAppearance(appearance)
    if (appearance.theme) setTheme(appearance.theme)
    // Solo cuando cambian las preferencias guardadas.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preferences, !!user])

  if (loading) {
    return (
      <div className="p-8">
        <Skeleton className="h-8 w-48" />
      </div>
    )
  }
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname }} />

  const logout = async () => {
    try {
      await post('/panel/auth/logout')
    } catch (e) {
      notifyError(e)
    }
    setClientScope(null)
    client.clear()
    window.location.assign('/login')
  }

  const visible = (item: NavItem) =>
    isOperator ? !!item.operator : item.audience === 'all' || (item.audience === 'manage' && canManage) || (item.audience === 'agency' && isAgency)

  return (
    <SidebarProvider>
      <Sidebar>
        <SidebarHeader>
          <div className="flex items-center gap-2.5 px-2 py-1.5">
            <BrandMark />
            <span className="min-w-0 leading-tight">
              <span className="block truncate font-semibold tracking-tight">Pixelapp</span>
              <span className="block truncate text-xs text-sidebar-foreground/70">{user.client_name ?? 'Pixel DC'}</span>
            </span>
          </div>
          {isAgency && <ClientScopeSelector />}
        </SidebarHeader>
        <SidebarContent>
          <SidebarGroup>
            <SidebarGroupContent>
              <SidebarMenu>
                {NAV.filter(visible).map((item) => (
                  <SidebarMenuItem key={item.to}>
                    <NavLink to={item.to} end={item.end}>
                      {({ isActive }) => (
                        <SidebarMenuButton render={<span />} isActive={isActive}>
                          <item.icon />
                          <span>{item.label}</span>
                          {item.to === '/bandeja' && <AttentionBadge />}
                        </SidebarMenuButton>
                      )}
                    </NavLink>
                  </SidebarMenuItem>
                ))}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        </SidebarContent>
        <SidebarFooter>
          <DropdownMenu>
            <DropdownMenuTrigger
              aria-label="Menú de usuario"
              className="flex w-full items-center gap-2.5 rounded-lg p-2 text-left outline-none transition hover:bg-sidebar-accent focus-visible:ring-2 focus-visible:ring-sidebar-ring"
            >
              <UserAvatar user={user} />
              <span className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-sm font-medium">{user.display_name?.trim() || user.email}</span>
                <span className="block truncate text-xs text-sidebar-foreground/70">{ROLE_LABEL[user.role]}</span>
              </span>
              <ChevronsUpDownIcon className="size-4 shrink-0 opacity-70" aria-hidden />
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" side="top" className="min-w-60">
              <DropdownMenuGroup>
                <DropdownMenuLabel className="truncate">{user.email}</DropdownMenuLabel>
              </DropdownMenuGroup>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={() => navigate('/cuenta')}>
                <ShieldCheckIcon /> Mi cuenta y seguridad
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => navigate('/apariencia')}>
                <PaletteIcon /> Perfil y apariencia
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => setTheme(document.documentElement.classList.contains('dark') || theme === 'dark' ? 'light' : 'dark')}>
                <SunIcon className="dark:hidden" />
                <MoonIcon className="hidden dark:block" /> Cambiar tema
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={logout}>
                <LogOutIcon /> Salir
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </SidebarFooter>
      </Sidebar>
      <SidebarInset>
        {isAgency && <ClientScopeBanner />}
        <header className="flex h-12 items-center gap-2 border-b px-4 md:hidden">
          <SidebarTrigger />
          <span className="truncate font-semibold tracking-tight">Pixelapp</span>
          <button type="button" onClick={() => navigate('/apariencia')} className="ml-auto rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring" aria-label="Perfil y apariencia">
            <UserAvatar user={user} size="sm" />
          </button>
        </header>
        <div className="w-full space-y-6 p-4 sm:p-6">
          <Outlet />
        </div>
      </SidebarInset>
    </SidebarProvider>
  )
}

const NoAccess = () => <p className="text-sm text-muted-foreground">No tienes permiso para ver esta sección.</p>

/** Clientes, integraciones y configuración: solo la agencia. */
export function RequireAgency() {
  const { isAgency } = useAuth()
  return isAgency ? <Outlet /> : <NoAccess />
}

/** Chatbots, conocimiento, equipo del cliente: la agencia y el administrador del cliente. */
export function RequireManager() {
  const { canManage } = useAuth()
  return canManage ? <Outlet /> : <NoAccess />
}

/** Pantallas generales: el operador no las ve y entra directo a la bandeja. */
export function RequireNotOperator() {
  const { isOperator } = useAuth()
  return isOperator ? <Navigate to="/bandeja" replace /> : <Outlet />
}
