import { Bot, MessagesSquare, Plug } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ThemeProvider } from '@/lib/theme'

// Pantalla provisional de la Fase 0: confirma la marca (colores, tipografía, componentes) hasta que lleguen las páginas del panel.
const pieces = [
  { icon: Bot, title: 'Chatbots', text: 'Clientes, assets y bots con su propio conocimiento.' },
  { icon: Plug, title: 'Integraciones', text: 'Claude, Meta y conexiones de datos por bot.' },
  { icon: MessagesSquare, title: 'Bandeja', text: 'Operadores que atienden cuando el bot no sabe.' },
]

export default function App() {
  return (
    <ThemeProvider>
      <main className="mx-auto flex min-h-svh max-w-4xl flex-col justify-center gap-8 px-4 py-12">
        <header className="space-y-3">
          <Badge variant="secondary">Pixel DC</Badge>
          <h1 className="text-4xl font-bold tracking-tight">Pixela</h1>
          <p className="text-muted-foreground max-w-xl">Plataforma de chatbots con IA para los clientes de Pixel DC.</p>
          <Button>Entrar al panel</Button>
        </header>
        <section className="grid gap-4 sm:grid-cols-3">
          {pieces.map(({ icon: Icon, title, text }) => (
            <Card key={title}>
              <CardHeader>
                <Icon className="text-primary size-5" aria-hidden />
                <CardTitle>{title}</CardTitle>
                <CardDescription>{text}</CardDescription>
              </CardHeader>
              <CardContent />
            </Card>
          ))}
        </section>
      </main>
    </ThemeProvider>
  )
}
