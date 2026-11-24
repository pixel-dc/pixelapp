# Pixela

Plataforma de chatbots con IA de **Pixel DC**: la agencia crea clientes, cada cliente tiene assets y cada asset sus chatbots.
Los bots responden en la web (widget) y por WhatsApp, Messenger e Instagram, con su propia llave de Claude y su propia app de Meta.

- Plan, decisiones y fases: [PLAN.md](PLAN.md)

## Requisitos
.NET SDK 10, Node 22 y PostgreSQL 17 o superior.

## Desarrollo local
```bash
cp .env.example .env        # completa Secrets__MasterKey y la conexión a PostgreSQL
dotnet tool restore         # dotnet-ef

dotnet run --project src/Pixela.Api        # API en http://localhost:5180
dotnet run --project src/Pixela.Workers    # procesos de fondo (indexado, canales de Meta, retención)

cd web && npm install && npm run dev       # panel en http://localhost:5173 (proxy a la API)
```

## Pruebas y calidad
```bash
dotnet test
dotnet format --verify-no-changes          # el formato es parte de la integración continua
cd web && npm run lint && npm test && npm run build
```
Las advertencias del compilador son errores (`TreatWarningsAsErrors`).

## Estructura
- `src/Pixela.Domain` entidades y reglas puras · `Application` casos de uso · `Infrastructure` EF Core/Npgsql, Claude, Meta, MCP
- `src/Pixela.Api` panel (`/panel`), chat público (`/chat/v1`, widget) y webhooks de Meta (`/meta/webhook/{bot}`)
- `src/Pixela.Workers` procesos de fondo
- `tests/` pruebas · `web/` panel React + Vite + Tailwind + shadcn/ui · `deploy/` servidor (Ubuntu 26.04, systemd, Caddy)
