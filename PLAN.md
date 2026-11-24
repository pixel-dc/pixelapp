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
| 7 | Datos | Se migra **todo** lo de **Don Porko** y **Pixel DC** desde FacilCorreo (bots, conocimiento, canales, conexiones, conversaciones, mensajes, adjuntos, contactos y consumo); el resto arranca en blanco. |
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
| 0 ✅ | Repo, solución `Pixela.*` en .NET 10, CI (build, format, pruebas), `.editorconfig`, `.gitignore`, README | `dotnet build` y CI en verde con proyectos vacíos |
| 1 ✅ | Dominio y persistencia: entidades de §2, `AppDbContext`, filtro por cliente, migración `InitialCreate` | Pruebas de dominio y una migración limpia |
| 2 ✅ | Servicios del chat copiados y adaptados; secretos por bot; SSRF compartido | Pruebas de chat/conocimiento/MCP en verde |
| 3 ✅ | Meta por bot: webhook `/{botKey}`, firma con la app del bot, token auto | Pruebas de canales en verde; verificación de Meta con un bot real |
| 4 ✅ | Auth y roles: agencia, ClientAdmin (gestiona admins y operadores), Operador; salto a cliente; primer arranque | Pruebas de permisos en verde |
| 5 ✅ | Panel con marca Pixel DC: clientes, assets, bots, pestaña Integraciones (Claude, Meta, MCP, Supabase), bandeja, uso | `npm run build/lint/test` en verde; revisión visual |
| 6 ✅ | Migración de datos: Don Porko y Pixel DC (ver §7) | Ambos bots responden en Pixela con su conocimiento |
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
2. Copia todo: bot (perfil, ícono, color, sitios permitidos, respuestas rápidas), fuentes y fragmentos de conocimiento, conexiones MCP, canales, conversaciones, mensajes, adjuntos (archivos del disco incluidos), contactos (leads) y consumo mensual (repartido por bot).
3. **Secretos**: los descifra con la llave maestra de FacilCorreo y los vuelve a cifrar con la de Pixela; nunca quedan en texto plano en disco.
4. La llave de Claude, que en FacilCorreo es por cuenta/agencia, se asigna a cada bot (o se crea una llave nueva por bot en la consola de Anthropic, recomendado para separar el consumo).
5. Para los canales de Meta hay que cambiar la URL del webhook y el token de verificación en la app de Meta de cada bot al hacer el cambio.

## 8. Decisiones de la Fase 1 (modelo de datos)

- **Aislamiento por cliente:** toda tabla de datos lleva `ClientId` y un filtro global de EF Core (`IClientScope`). Un usuario de cliente solo ve su cliente; la agencia ve todo o el cliente que elija en el selector; sin alcance resuelto no se ve nada.
- **Usuarios:** `ClientId` nulo = agencia (`AgencyOwner`, `AgencyAdmin`); con cliente = `ClientAdmin` u `Operator`. La base lo garantiza con la restricción `ck_users_role_client`. Se quitan el pase de salto entre subdominios y las pertenencias múltiples de FacilCorreo: la agencia entra a un cliente con el selector (`UserSession.ActingClientId`).
- **Secretos del bot** en la tabla `bot_secrets` (aparte de `bots`, para que ninguna consulta los arrastre): llave de Claude, clave secreta de la app de Meta y token de verificación. El canal ya no guarda clave de app propia.
- **Consumo por bot y mes** (`chat_usage`, llave `BotId + Period`): se conserva aunque se borre el bot, para cuadrarlo con la consola de Anthropic. El del cliente es la suma.
- **Topes y precio** viven en `clients` (`PricePerConversation`, `MonthlyConversationCap`, `MessagesPerConversationCap`); null = ilimitado.
- **Configuración de la agencia** en `agency_settings` (una fila, la crea el arranque).

## 9. Decisiones de la Fase 2 (motor del chat)

- **Llave de Claude solo por bot** (`BotKeyService`): sin respaldo de agencia ni de servidor. Sin llave, el bot responde en modo extractivo (sin modelo, sin costo) y el gateway de Claude se niega a llamar.
- **Modelo, esfuerzo y precios** salen de `agency_settings` (el modelo del bot manda); `AiOptions` solo guarda ajustes técnicos (URL, tiempos, reintentos, rondas de herramientas).
- **Topes:** el tope mensual del cliente suma las conversaciones de todos sus bots; sin tope, ilimitado. Sin tope de mensajes por conversación rige un techo de seguridad de 1.000 (corta bucles entre bots).
- **Firma de las sesiones del chat** con una llave derivada de la maestra (HKDF, propósito `chat-session`): ya no hace falta `ApiKeys:Pepper`.
- **Webhook de Meta por bot:** `MetaInboxService.ResolveBotAppAsync(clave pública)` da la app del bot (clave secreta y token de verificación) y `AcceptAsync` solo acepta mensajes de canales de ese bot. El token de verificación se genera al crear el bot (`pxv_…`). Los endpoints `/meta/webhook/{clave}` llegan en la Fase 3.
- **SSRF** en `Application/Http/OutboundHttp.cs` (validador) e `Infrastructure/Http/SafeConnect.cs` (conexión), compartido por páginas web, Supabase y MCP.
- Lo que se crea desde la vista de agencia (fuentes, conexiones) hereda el cliente del bot; la búsqueda por SQL respeta la vista de agencia.

## 10. Decisiones de la Fase 3 (Meta por bot)

- **Webhook por bot:** `GET/POST /meta/webhook/{clave pública del bot}`. La verificación compara con el token del bot; la firma se valida con la clave secreta de la app de ese bot. Bot sin app de Meta = todo POST rechazado (401). Un mensaje de un canal de otro bot, aunque llegue bien firmado, no entra.
- **`BotMetaAppService`:** muestra la URL del webhook (con `agency_settings.PublicBaseUrl`) y el token para copiarlos en Meta; guarda o quita la clave secreta de la app; regenera el token si se filtró. Los endpoints del panel para esto llegan con la autenticación (Fase 4).
- **Workers:** `MetaInboxWorker` (responde por el canal) y `MaintenanceService` (retención de conversaciones según la agencia, archivos vencidos y registro de Meta).
- **Caddy** debe enviar `/meta/*` a la API (Fase 7).
- Pruebas: las ~35 del webhook de FacilCorreo, adaptadas a una app por bot.

## 11. Decisiones de la Fase 4 (acceso y roles)

- **Sesión por cookie** `px_session` (HttpOnly, SameSite=Strict, Secure en producción) y cabecera anti-CSRF `X-Requested-With: Pixela` en todo lo que modifica datos. Contraseñas Argon2id, bloqueo por intentos, 2FA opcional con códigos de recuperación (firmados con llaves derivadas de la maestra).
- **Roles y políticas:** `panel:session` (cualquiera, incluida la bandeja del operador), `panel:client-admin` (administrador del cliente y agencia: conocimiento, contactos, uso, respuestas rápidas, equipo del cliente), `panel:agency` (clientes, assets, bots, llave de Claude, app de Meta, canales, conexiones, configuración) y `panel:agency-owner` (equipo de la agencia).
- **Selector de cliente:** la agencia envía `X-Client-Id` para trabajar con un cliente; sin él ve todos. Un usuario de cliente siempre queda en el suyo (pedir otro da 403).
- **Equipos:** el administrador de un cliente invita y gestiona administradores y operadores de su cliente; la agencia también, eligiendo el cliente. El equipo de la agencia solo lo cambia su dueño; siempre queda un dueño activo.
- **Cliente suspendido:** sus usuarios no entran y sus sesiones dejan de valer; sus bots no atienden.
- **Primer arranque:** `GET/POST /panel/setup` crea al dueño de la agencia mientras no exista ningún usuario (con candado en la base); después queda cerrado. Ya no hay usuario ni contraseña en variables de entorno.
- **Mover un bot** a otro asset (de otro cliente incluso) lleva consigo secretos, conocimiento, canales, conversaciones, mensajes, adjuntos y contactos; el consumo de meses anteriores se queda donde ocurrió.
- Se quitaron de FacilCorreo: subdominios por cliente, pase de acceso entre subdominios, pertenencias múltiples y consola de superadmin.

## 12. Decisiones de la Fase 5 (panel web)

- Pantallas traídas de FacilCorreo (bandeja, conocimiento, canales, conexiones, respuestas rápidas, consumo, equipo, cuenta, apariencia) y nuevas: primer arranque (`/configurar`), resumen, clientes y ficha del cliente (assets con sus chatbots, datos, marca del widget, precio y topes), configuración de la agencia, equipo de la agencia y pestaña **Integraciones** del bot (llave de Claude con prueba; app de Meta con URL del webhook, token de verificación y clave secreta).
- Menú por rol: el operador solo ve la bandeja y su cuenta; el administrador del cliente, chatbots, bandeja y su equipo; la agencia, todo, con el selector «Estás viendo» (cabecera `X-Client-Id` en las rutas de datos, nunca en sesión, cuenta ni configuración de la agencia).
- Marca: acento por defecto cian `#3AD3F8`, Poppins, colores de Pixel DC en el selector de color. El logo se toma de `web/public/pixela-logo.png` si existe; si no, el distintivo «DC».
- Textos legales: falta la razón social y el correo de contacto de Pixel DC (marcados como pendientes en `web/src/pages/legal.tsx`).
- Revisado en navegador contra API y PostgreSQL reales: primer arranque, entrada, crear cliente con asset, crear bot, Integraciones; escritorio y teléfono, tema oscuro y claro.

## 13. Decisiones de la Fase 6 (migración)

- Herramienta `tools/Pixela.Migrator` (guía en su `README.md`). Lee FacilCorreo en una transacción de solo lectura y escribe en Pixela en una sola transacción; `--dry-run` muestra el informe sin guardar.
- Conserva ids y **claves públicas** de los bots (el widget instalado solo cambia de dominio). La agencia Pixel DC también queda como cliente con su bot, y sus dueños/administradores pasan al equipo de la agencia.
- Secretos recifrados con la llave de Pixela. Cada bot recibe un token de verificación de Meta nuevo: hay que actualizar el webhook en Meta al cambiar.
- El consumo mensual por cuenta pasa al bot (al más antiguo si había varios). Los códigos de recuperación del 2FA no se migran.
- Probada con el esquema real de FacilCorreo (`tests/Pixela.Migrator.Tests/facilcorreo-schema.sql`, generado de sus migraciones el 2026-11-24). Si FacilCorreo cambia su esquema antes de migrar, regenerar ese archivo y volver a correr las pruebas.

## 14. Pendientes

- Dominio del panel de Pixela (¿`chat.pixeldc.com`, `bots.pixeldc.com`…?) y acceso a su DNS.
- Logo de Pixela (robot «DC» + «PIXEL@», recibido en el chat): guardarlo como `web/public/pixela-logo.png` (el panel ya lo usa si existe); idealmente también en SVG.
- Datos legales de Pixel DC para las páginas de privacidad y términos: razón social y correo de contacto.
- Dónde vive el repositorio (GitHub de Pixel DC u otro) para el despliegue desde Git.
- Para correr la migración real: usuario de solo lectura de la base de FacilCorreo, su llave maestra y (opcional) la carpeta de adjuntos.
