import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { lazy, Suspense } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { AppShell, RequireAgency, RequireManager, RequireNotOperator } from '@/components/app-shell'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { ApiError } from '@/lib/api'
import { AuthProvider } from '@/lib/auth'
import { ThemeProvider } from '@/lib/theme'

const PrivacyPage = lazy(() => import('@/pages/legal').then((m) => ({ default: m.PrivacyPage })))
const TermsPage = lazy(() => import('@/pages/legal').then((m) => ({ default: m.TermsPage })))
const DataDeletionPage = lazy(() => import('@/pages/legal').then((m) => ({ default: m.DataDeletionPage })))
const SetupPage = lazy(() => import('@/pages/setup'))
const LoginPage = lazy(() => import('@/pages/login'))
const AcceptInvitePage = lazy(() => import('@/pages/accept-invite'))
const ResetPasswordPage = lazy(() => import('@/pages/reset-password'))
const DashboardPage = lazy(() => import('@/pages/dashboard'))
const ClientsPage = lazy(() => import('@/pages/clients'))
const ClientDetailPage = lazy(() => import('@/pages/client-detail'))
const BotsPage = lazy(() => import('@/pages/bots'))
const BotDetailPage = lazy(() => import('@/pages/bot-detail'))
const InboxPage = lazy(() => import('@/pages/inbox'))
const TeamPage = lazy(() => import('@/pages/team'))
const AgencyTeamPage = lazy(() => import('@/pages/team').then((m) => ({ default: m.AgencyTeamPage })))
const AgencySettingsPage = lazy(() => import('@/pages/agency-settings'))
const AccountPage = lazy(() => import('@/pages/account'))
const AppearancePage = lazy(() => import('@/pages/appearance'))

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      refetchOnWindowFocus: false,
      // No se reintenta lo que el servidor rechazó de forma definitiva (4xx).
      retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
    },
  },
})

export function AppRoutes() {
  return (
    <Suspense fallback={null}>
      <Routes>
        <Route path="/privacidad" element={<PrivacyPage />} />
        <Route path="/terminos" element={<TermsPage />} />
        <Route path="/eliminacion-de-datos" element={<DataDeletionPage />} />
        <Route path="/configurar" element={<SetupPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/invitacion" element={<AcceptInvitePage />} />
        <Route path="/restablecer" element={<ResetPasswordPage />} />
        <Route element={<AppShell />}>
          <Route path="cuenta" element={<AccountPage />} />
          <Route path="apariencia" element={<AppearancePage />} />
          <Route path="bandeja" element={<InboxPage />} />
          <Route element={<RequireNotOperator />}>
            <Route index element={<DashboardPage />} />
            <Route element={<RequireManager />}>
              <Route path="chatbots" element={<BotsPage />} />
              <Route path="chatbots/:id" element={<BotDetailPage />} />
              <Route path="equipo" element={<TeamPage />} />
            </Route>
            <Route element={<RequireAgency />}>
              <Route path="clientes" element={<ClientsPage />} />
              <Route path="clientes/:id" element={<ClientDetailPage />} />
              <Route path="agencia/equipo" element={<AgencyTeamPage />} />
              <Route path="agencia/configuracion" element={<AgencySettingsPage />} />
            </Route>
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </Suspense>
  )
}

export default function App() {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <TooltipProvider>
            <BrowserRouter>
              <AppRoutes />
            </BrowserRouter>
            <Toaster richColors closeButton />
          </TooltipProvider>
        </AuthProvider>
      </QueryClientProvider>
    </ThemeProvider>
  )
}
