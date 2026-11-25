export type Role = 'agency_owner' | 'agency_admin' | 'client_admin' | 'operator'

export const ROLE_LABEL: Record<Role, string> = {
  agency_owner: 'Dueño de la agencia',
  agency_admin: 'Equipo de la agencia',
  client_admin: 'Administrador',
  operator: 'Operador',
}

export interface User {
  id: string
  /** Cliente del usuario; null = equipo de la agencia. */
  client_id: string | null
  client_name: string | null
  email: string
  role: Role
  two_factor_enabled: boolean
  display_name?: string | null
  /** Cambia con cada foto nueva (sirve para no cachear la anterior); null = sin foto. */
  avatar_version?: number | null
  /** Apariencia elegida; se valida con sanitize() antes de usarla. */
  preferences?: Record<string, unknown> | null
}

export interface TeamMember {
  id: string
  email: string
  role: Role
  is_active: boolean
  two_factor_enabled: boolean
  created_at: string
  last_login_at: string | null
  display_name?: string | null
  avatar_version?: number | null
}

export interface PendingInvite {
  id: string
  email: string
  role: Role
  expires_at: string
}

export interface AgencySettings {
  name: string
  logo_url: string | null
  primary_color: string | null
  texts: Record<string, string>
  default_model: string
  effort: 'low' | 'medium' | 'high'
  input_usd_per_million: number
  output_usd_per_million: number
  chat_retention_days: number
  public_base_url: string | null
  updated_at: string
}

export interface Client {
  id: string
  name: string
  slug: string
  status: 'active' | 'suspended'
  logo_url: string | null
  primary_color: string | null
  texts: Record<string, string>
  /** Null = no se cobra. */
  price_per_conversation: number | null
  currency: string
  /** Null = ilimitado. */
  monthly_conversation_cap: number | null
  messages_per_conversation_cap: number | null
  created_at: string
  assets: number
  bots: number
  conversations_this_month: number
}

export interface Asset {
  id: string
  client_id: string
  name: string
  description: string | null
  created_at: string
  bots: number
}

export interface Bot {
  id: string
  public_key: string
  name: string
  system_prompt: string | null
  tone: string | null
  welcome_message: string | null
  farewell?: string | null
  handoff_message?: string | null
  /** Horas sin actividad de una persona tras las que el bot retoma la conversación; 0 = solo se devuelve a mano. */
  handoff_return_hours?: number
  /** Cambia con cada icono nuevo; null = sin icono. */
  icon_version?: number | null
  call_to_action?: string | null
  address?: string | null
  /** «latitud, longitud». */
  coordinates?: string | null
  whatsapp_number?: string | null
  color: string | null
  privacy_url: string | null
  allowed_origins: string[]
  is_active: boolean
  created_at: string
  asset_id?: string | null
}

export interface BotList {
  bots: Bot[]
  /** Conversaciones de canales de Meta que esperan a una persona. */
  needing_attention?: number
}

/** Llave de Claude del bot: nunca llega completa, solo sus últimos caracteres. */
export interface ClaudeKey {
  configured: boolean
  hint: string | null
  set_at: string | null
}

/** App de Meta del bot: lo que se copia en Meta y si ya tiene la clave secreta. */
export interface MetaApp {
  webhook_url: string
  verify_token: string
  has_app_secret: boolean
  app_secret_hint: string | null
  updated_at: string
}

export interface KnowledgeSource {
  id: string
  kind: 'faq' | 'text' | 'pdf' | 'url' | 'supabase' | 'products'
  title: string
  source_url: string | null
  /** Solo catálogos de Supabase. La llave nunca llega al panel. */
  table?: string | null
  columns?: string | null
  refresh_minutes?: number | null
  link_template?: string | null
  image_column?: string | null
  synced_at?: string | null
  status: 'pending' | 'indexed' | 'failed'
  error: string | null
  chunks: number
  created_at: string
}

export interface KnowledgeHit {
  text: string
  source: string
  rank: number
}

export interface ConversationSummary {
  id: string
  first_message: string | null
  messages: number
  started_at: string
  last_message_at: string
  /** whatsapp, messenger o instagram; null en el chat web. */
  channel: string | null
  contact: string | null
  needs_attention: boolean
  /** Una persona del negocio atiende la conversación y el bot no responde. */
  human_active?: boolean
  /** El negocio todavía puede escribirle al contacto (canal activo y menos de 24 h desde su último mensaje). */
  can_reply?: boolean
  /** Solo en la bandeja única: chatbot al que pertenece. */
  bot_id?: string | null
  bot_name?: string | null
  /**
   * En un canal, la lista muestra un solo hilo por contacto: cuántas conversaciones de 24 horas lo forman. `id` es la más reciente (a la que se
   * responde); `messages` y `started_at` abarcan todo el historial del contacto.
   */
  sessions?: number
}

/** Canal de Meta conectado a un chatbot. El token nunca llega al panel. */
export interface Channel {
  id: string
  kind: 'whatsapp' | 'messenger' | 'instagram'
  external_id: string
  display_name: string
  status: 'active' | 'disabled' | 'tokenerror'
  created_at: string
  /** ID del WABA (solo canales de WhatsApp). */
  account_id?: string | null
}

export interface ConnectionCheck {
  id: string
  channel_id: string | null
  title: string
  status: 'ok' | 'warning' | 'error'
  detail: string | null
  /** Pixela puede corregirlo sola con «Reparar». */
  fixable: boolean
  /** Qué hacer a mano cuando Pixela no puede corregirlo. */
  hint: string | null
}

export interface ConnectionReport {
  webhook_url: string
  healthy: boolean
  can_repair: boolean
  checks: ConnectionCheck[]
}

export interface AppliedFix {
  title: string
  success: boolean
  message: string | null
}

export interface TranscriptAttachment {
  id: string
  /** image, document, audio, video o sticker. */
  kind: string
  mime_type: string | null
  file_name: string | null
  size_bytes: number
  /** Falso si ya venció (se conservan pocos días) o no se pudo descargar. */
  available: boolean
  /** Conversación a la que pertenece el archivo: en un hilo con varias, se abre por la suya. */
  conversation_id?: string | null
}

export interface TranscriptMessage {
  /** user = el contacto, assistant = el bot, agent = una persona del negocio. */
  role: 'user' | 'assistant' | 'agent'
  content: string
  created_at: string
  attachments?: TranscriptAttachment[] | null
  /** Conversación de 24 horas a la que pertenece: cambia donde empieza una nueva. */
  conversation_id?: string | null
}

export interface Lead {
  id: string
  conversation_id: string | null
  name: string
  email: string | null
  phone: string | null
  interest: string | null
  created_at: string
}

export interface BotUsageTotals {
  replies: number
  tokens_in: number
  tokens_out: number
  /** Millonésimas de dólar. */
  cost_micros: number
}

export interface BotUsage {
  month: BotUsageTotals
  last_30_days: BotUsageTotals
  retained: BotUsageTotals
  retention_days: number
  by_channel: (BotUsageTotals & { channel: string })[]
  days: (BotUsageTotals & { day: string })[]
  input_usd_per_million: number
  output_usd_per_million: number
}

/** Textos del widget que la agencia o cada cliente pueden cambiar (lista cerrada). */
export const WIDGET_TEXTS: { key: string; label: string; placeholder: string }[] = [
  { key: 'placeholder', label: 'Campo de mensaje', placeholder: 'Escribe tu mensaje…' },
  { key: 'send', label: 'Botón de enviar', placeholder: 'Enviar' },
  { key: 'typing', label: 'Mientras responde', placeholder: 'Escribiendo…' },
  { key: 'lead_link', label: 'Enlace para dejar datos', placeholder: 'Déjanos tus datos' },
  { key: 'lead_name', label: 'Campo nombre', placeholder: 'Tu nombre' },
  { key: 'lead_email', label: 'Campo correo', placeholder: 'Tu correo' },
  { key: 'lead_phone', label: 'Campo teléfono', placeholder: 'Tu teléfono' },
  { key: 'lead_interest', label: 'Campo interés', placeholder: '¿En qué te podemos ayudar?' },
  { key: 'consent', label: 'Autorización de datos', placeholder: 'Autorizo el tratamiento de mis datos' },
  { key: 'thanks', label: 'Agradecimiento', placeholder: '¡Gracias! Te contactaremos pronto.' },
]
