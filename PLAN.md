# Pixela — Plataforma de chatbots IA de Pixel DC

Plan para separar la parte de chatbots de FacilCorreo en un repositorio propio para Pixel DC (agencia única).
Fecha: 2026-11-24.

## 1. Decisiones tomadas

| # | Tema | Decisión |
|---|---|---|
| 1 | Token de verificación de Meta | **Uno por bot**, generado automáticamente al crearlo. Cada bot tiene su propia URL de webhook. |
| 2 | Jerarquía | Pixel DC (agencia única) → **Cliente** → **Asset** (agrupa los chatbots de un mismo cliente) → **Bot**. |
| 3 | App de Meta | **Cada bot trae su app**: clave secreta de la app, token de verificación y tokens de acceso de cada canal, todo por bot. |
| 4 | Llave de Claude | **Una por bot**, obligatoria para responder con modelo. Así Pixel DC audita el consumo en la consola de Anthropic (por llave) y dentro de Pixela (tokens y costo por bot). Sin respaldo global. |
| 5 | Topes y cobro | Se conservan precio por conversación y topes por cliente, pero **por defecto ilimitados**. Se documenta cómo configurarlos. |
| 6 | Marca | Pixel DC (ver §6). |
| 7 | Datos | Se migran los bots de **Don Porko** y **Pixel DC** desde FacilCorreo; el resto arranca en blanco. |
| 8 | Infraestructura | Droplet propio de DigitalOcean con **Ubuntu 26.04**, PostgreSQL en el host, **sin Docker** (servicios systemd + Caddy), despliegue desde Git. **.NET 10 LTS**. |

## 2. Modelo de datos

```
Agency (una fila: Pixel DC) ── AgencySettings (modelo por defecto, precios de referencia, marca)
  └─ Client (nombre, slug, estado, logo/color/textos del widget, precio por conversación?, tope mensual?)
       └─ Asset (nombre, descripción)
            └─ Bot
                 ├─ BotSecrets (cifrado AES-GCM): ClaudeApiKey, MetaAppSecret, MetaVerifyToken (auto)
                 ├─ Channel (WhatsApp / Messenger / Instagram: ExternalId + AccessToken cifrado)
                 ├─ KnowledgeSource / KnowledgeChunk (FAQ, texto, URL, PDF, productos, Supabase)
                 ├─ DataConnection (MCP)
                 └─ Conversation / ChatMessage / ChatAttachment / Lead / ChatUsage
Users: User, UserSession, UserInvite, PasswordResetToken, UserMembership, UserAvatar
```

**Roles**
- **Agencia** (usuarios de Pixel DC): `Owner` y `Admin`. Crean clientes, assets y bots, configuran secretos e integraciones y entran a cualquier cliente (salto con pase de 60 s, auditado).
- **Cliente**: `ClientAdmin` invita y gestiona **administradores y operadores** de su cliente; alimenta el conocimiento, ve conversaciones y uso. No ve ni cambia secretos ni integraciones (lo hace la agencia; se puede abrir más adelante).
- **Operador**: solo la bandeja (leer, tomar, responder, devolver al bot) de los bots de su cliente.

Se elimina: `TenantKind` Platform/Standalone, `SuperAdmin`, `Plan`, `AgencyChatRate` (tarifa de la plataforma a la agencia), multi-agencia.
`Tenant.AiKeyEncrypted` pasa a `BotSecrets.ClaudeApiKey`.

## 3. Qué se lleva de FacilCorreo

**Tal cual (renombrando el espacio de nombres `FacilCorreo` → `Pixela`)**
- Application/Chat: `ChatService`, `KnowledgeService`, `Chunker`, `CatalogText`, `ContextBlock`, `ConversationService`, `BotProfile`, `BotUsageService`, `DataConnections`, `AttachmentStore`, `MetaInbox`, `MetaChannels`, `ChannelService`, `AiGateway`.
- Infrastructure/Chat: `AnthropicGateway`, `McpHttpClient`, `MetaGraphClient`, `KnowledgeInfrastructure` (búsqueda en PostgreSQL, PDF, páginas web), `SupabaseCatalogFetcher`, `LocalAttachmentStore`, `ChannelInboundClaimer`.
- Seguridad y sesiones: `AesGcmSecretProtector`, `Argon2PasswordHasher`, `AuthService`, `UserService`, `ProfileService`, `Totp`, `SessionAuthenticationHandler`, `ProxyForwarding`.
- Api: `ChatEndpoints`, `WidgetAssets`, `BotEndpoints`, `MetaEndpoints`, partes de `PanelEndpoints` (login, sesión, equipo, perfil), `MonitoringEndpoints` (salud).
- Workers: `KnowledgeIndexWorker`, `MetaInboxWorker`, `MaintenanceService` (solo retención del chat), `HeartbeatService`.
- Web: `index.css`, `components/ui/*`, `app-shell`, tema y apariencia, páginas de bots, conocimiento, canales, conexiones, bandeja, uso, equipo, cuenta, login, invitación, acceso, restablecer y legales.
- Pruebas de chat, bots, canales, conocimiento, bandeja, adjuntos, marca y permisos.

**Se adapta**
- `SafeWebhookHttp` y `WebhookUrlValidator` (protección SSRF) salen del módulo de webhooks a `Infrastructure/Http/SafeHttp`.
- `TenantLimits`, `AgencyService`, `AgencyChatService`, `AgencyBrandService`, `ChatBillingService`, `TransferService` (mover bots entre assets/clientes): se reescriben sobre Agency → Client → Asset.
- `AiKeyService`: resuelve la llave del bot; sin llave el bot responde en modo extractivo y el panel lo avisa.
- `MetaEndpoints`: `GET/POST /meta/webhook/{botKey}` valida con el token y la clave secreta del bot.
- Marca: cookie `px_session`, cabecera CSRF `X-Requested-With: Pixela`, emisor 2FA `Pixela`, páginas legales a nombre de Pixel DC.

**Se queda en FacilCorreo**: correo (envío, SMTP, rastreo, rebotes, supresiones, dominios/DKIM, webhooks y API keys de correo), proyecto Tracker, consola de plataforma, planes, las 73 migraciones (Pixela empieza con una `InitialCreate`).

## 4. Configuración: el `.env` queda mínimo

`/etc/pixela/pixela.env`:
```
Secrets__MasterKey=<32 bytes base64>     # cifra todos los secretos guardados en la base
ConnectionStrings__Default=Host=localhost;Database=pixela;Username=pixela;Password=...
```
Todo lo demás vive en la base y se edita desde el panel:
- Por bot: llave de Claude, clave secreta de la app de Meta, token de verificación (auto), tokens de canal, conexiones MCP, llave de Supabase.
- `AgencySettings`: modelo por defecto, esfuerzo, precios de referencia del modelo (para el costo estimado), retención, límites por IP, dominio del panel.
- Primer usuario: asistente de primer arranque (`/setup`, solo disponible mientras no exista ningún usuario) o `pixela-admin create-owner`.
- Valores no secretos con defaults en `appsettings.json` (versionado).

## 5. Fases

| Fase | Contenido | Hecho cuando |
|---|---|---|
| 0 | Repo, solución `Pixela.*` en .NET 10, CI (build, format, pruebas), `.editorconfig`, `.gitignore`, README | `dotnet build` y CI en verde con proyectos vacíos |
| 1 | Dominio y persistencia: entidades de §2, `AppDbContext`, filtro por cliente, migración `InitialCreate` | Pruebas de dominio y una migración limpia |
| 2 | Servicios del chat copiados y adaptados; secretos por bot; SSRF compartido | Pruebas de chat/conocimiento/MCP en verde |
| 3 | Meta por bot: webhook `/{botKey}`, firma con la app del bot, token auto | Pruebas de canales en verde; verificación de Meta con un bot real |
| 4 | Auth y roles: agencia, ClientAdmin (gestiona admins y operadores), Operador; salto a cliente; primer arranque | Pruebas de permisos en verde |
| 5 | Panel con marca Pixel DC: clientes, assets, bots, pestaña Integraciones (Claude, Meta, MCP, Supabase), bandeja, uso | `npm run build/lint/test` en verde; revisión visual |
| 6 | Migración de datos: Don Porko y Pixel DC (ver §7) | Ambos bots responden en Pixela con su conocimiento |
| 7 | Despliegue: droplet Ubuntu 26.04, PostgreSQL, Caddy, systemd, script de despliegue desde Git, respaldos | Panel, widget y webhook de Meta funcionando en el dominio de Pixel DC |
| 8 | Documentación y entrega: README, guía de Meta por bot, manual de topes y cobro, manual de despliegue | Pixel DC despliega un cambio por su cuenta |

## 6. Marca Pixel DC (de pixeldc.com)

- **Logo**: wordmark «PIXEL» + «DC» en recuadro oscuro.
  - Logo negro: `https://pixeldc.com/wp-content/uploads/2026/08/Logo-pixel-dc-2026-Negro-scaled.png`
  - Ícono: `https://pixeldc.com/wp-content/uploads/2026/08/Icono-PIXEL-DC.png`
  - Pendiente pedir a Pixel DC la versión en SVG y la versión blanca (para el modo oscuro).
- **Colores**: base casi negra `#111111` / azul noche `#1E1E2F`; acento **cian `#3AD3F8`** (botones y llamados a la acción); acento secundario lavanda `#CEBFFF`; grises `#333333`, `#555555`, `#EAECF0`, `#F7F7F7`; blanco.
- **Tipografía**: **Poppins** (texto y títulos) y Plus Jakarta Sans.
- **Aplicación en el panel**: misma estructura de tokens de FacilCorreo (shadcn), cambiando `--primary` a cian con texto oscuro (`#3AD3F8` sobre `#111111` cumple contraste; blanco sobre cian no), barra lateral oscura `#1E1E2F`, fuente Poppins en lugar de Geist. El widget conserva el color propio de cada bot/cliente.

## 7. Migración de Don Porko y Pixel DC

Script de una sola vez (`tools/Pixela.Migrator`) que lee la base de FacilCorreo y escribe en la de Pixela:
1. Crea Cliente y Asset para cada uno.
2. Copia el bot (perfil, ícono, color, sitios permitidos), fuentes de conocimiento (y reindexa), conexiones MCP y canales.
3. **Secretos**: los descifra con la llave maestra de FacilCorreo y los vuelve a cifrar con la de Pixela; nunca quedan en texto plano en disco.
4. La llave de Claude, que en FacilCorreo es por cuenta/agencia, se asigna a cada bot (o se crea una llave nueva por bot en la consola de Anthropic, recomendado para separar el consumo).
5. Para los canales de Meta hay que cambiar la URL del webhook y el token de verificación en la app de Meta de cada bot al hacer el cambio.

## 8. Pendientes

- Instalar el **SDK de .NET 10** en esta máquina (hoy solo está el 9.0.302).
- Dominio del panel de Pixela (¿`chat.pixeldc.com`, `bots.pixeldc.com`…?) y acceso a su DNS.
- ¿Se migran también conversaciones y contactos de Don Porko y Pixel DC, o solo configuración y conocimiento?
- Logo en SVG y versión blanca.
- Dónde vive el repositorio (GitHub de Pixel DC u otro) para el despliegue desde Git.
- Acceso de solo lectura a la base de producción de FacilCorreo para la migración.
