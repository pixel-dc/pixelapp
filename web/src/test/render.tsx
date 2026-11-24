import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { AppRoutes } from '@/App'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { AuthProvider } from '@/lib/auth'
import { ThemeProvider } from '@/lib/theme'

/** La aplicación completa en memoria, empezando en `path`. Sin reintentos para que los errores se vean al instante. */
export function renderApp(path = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0 }, mutations: { retry: false } } })
  return render(
    <ThemeProvider>
      <QueryClientProvider client={client}>
        <AuthProvider>
          <TooltipProvider>
            <MemoryRouter initialEntries={[path]}>
              <AppRoutes />
            </MemoryRouter>
            <Toaster />
          </TooltipProvider>
        </AuthProvider>
      </QueryClientProvider>
    </ThemeProvider>,
  )
}
