import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { setClientScope } from '@/lib/client-scope'
import { renderApp } from '@/test/render'
import { ME, mockApi } from '@/test/server'

const BOT = {
  id: 'b1',
  public_key: 'bt_abc',
  name: 'Porky',
  system_prompt: null,
  tone: null,
  welcome_message: null,
  color: null,
  privacy_url: null,
  allowed_origins: [],
  is_active: true,
  created_at: '2026-11-24T00:00:00Z',
  asset_id: 'a1',
}
const CLIENT = {
  id: 'c1',
  name: 'Don Porko',
  slug: 'don-porko',
  status: 'active',
  logo_url: null,
  primary_color: null,
  texts: {},
  price_per_conversation: null,
  currency: 'COP',
  monthly_conversation_cap: null,
  messages_per_conversation_cap: null,
  created_at: '2026-11-24T00:00:00Z',
  assets: 1,
  bots: 1,
  conversations_this_month: 12,
}
const ASSET = { id: 'a1', client_id: 'c1', name: 'Restaurante', description: null, created_at: '2026-11-24T00:00:00Z', bots: 1 }

afterEach(() => setClientScope(null))

describe('primer arranque', () => {
  it('sin usuarios, la entrada lleva a crear al dueño de la agencia', async () => {
    const api = mockApi({
      'GET /panel/me/': { status: 401, body: { error: { code: 'unauthorized', message: 'x' } } },
      'GET /panel/setup': { body: { pending: true } },
      'POST /panel/setup': { status: 201, body: { ok: true } },
    })
    renderApp('/login')

    expect(await screen.findByText('Bienvenido a Pixelapp')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'Pixelapp' })).toHaveAttribute('src', '/pixela-logo.png')
    await userEvent.type(screen.getByLabelText('Correo'), 'dueno@pixeldc.test')
    await userEvent.type(screen.getByLabelText('Contraseña'), 'una frase larga de prueba')
    await userEvent.type(screen.getByLabelText('Repite la contraseña'), 'una frase larga de prueba')
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta de la agencia' }))

    await waitFor(() => expect(api.find('POST', '/panel/setup')).toHaveLength(1))
    expect(api.find('POST', '/panel/setup')[0].body).toMatchObject({ email: 'dueno@pixeldc.test' })
    expect(api.find('POST', '/panel/setup')[0].headers['X-Requested-With']).toBe('Pixela')
  })
})

describe('menú por rol', () => {
  it('el operador solo ve la bandeja y su cuenta', async () => {
    mockApi({
      'GET /panel/me/': { body: { user: ME.operator } },
      'GET /panel/bots/': { body: { bots: [], needing_attention: 0 } },
      'GET /panel/bots/inbox': { body: { conversations: [] } },
    })
    renderApp('/')

    const nav = await screen.findByRole('list')
    await waitFor(() => expect(within(nav).getByText('Bandeja')).toBeInTheDocument())
    expect(within(nav).queryByText('Chatbots')).not.toBeInTheDocument()
    expect(within(nav).queryByText('Clientes')).not.toBeInTheDocument()
    expect(within(nav).getByText('Mi cuenta')).toBeInTheDocument()
    expect(document.querySelector('img[src="/pixela-icon.png"]')).not.toBeNull()
  })

  it('el administrador del cliente ve chatbots y su equipo, no clientes ni configuración', async () => {
    mockApi({
      'GET /panel/me/': { body: { user: ME.clientAdmin } },
      'GET /panel/bots/': { body: { bots: [BOT], needing_attention: 0 } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
    })
    renderApp('/')

    expect(await screen.findByText(/Así van los chatbots de Don Porko/)).toBeInTheDocument()
    const nav = screen.getAllByRole('list')[0]
    expect(within(nav).getByText('Chatbots')).toBeInTheDocument()
    expect(within(nav).getByText('Equipo del cliente')).toBeInTheDocument()
    expect(within(nav).queryByText('Clientes')).not.toBeInTheDocument()
    expect(within(nav).queryByText('Configuración')).not.toBeInTheDocument()
  })
})

describe('agencia', () => {
  it('con un cliente elegido, las rutas de datos llevan X-Client-Id y las de la agencia no', async () => {
    setClientScope('c1')
    const api = mockApi({
      'GET /panel/me/': { body: { user: ME.agencyAdmin } },
      'GET /panel/bots/': { body: { bots: [BOT], needing_attention: 0 } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
      'GET /panel/agency/settings': {
        body: {
          settings: {
            name: 'Pixel DC', logo_url: null, primary_color: null, texts: {}, default_model: 'claude-haiku-5-5', effort: 'low',
            input_usd_per_million: 0.1, output_usd_per_million: 0.5, chat_retention_days: 90, public_base_url: null, updated_at: '2026-11-24T00:00:00Z',
          },
        },
      },
    })
    renderApp('/agencia/configuracion')

    expect(await screen.findByLabelText('Nombre de la agencia')).toHaveValue('Pixel DC')
    await waitFor(() => expect(api.find('GET', '/panel/bots/').length).toBeGreaterThan(0))
    expect(api.find('GET', '/panel/bots/')[0].headers['X-Client-Id']).toBe('c1')
    expect(api.find('GET', '/panel/agency/settings')[0].headers['X-Client-Id']).toBeUndefined()
    expect(api.find('GET', '/panel/me/')[0].headers['X-Client-Id']).toBeUndefined()
    expect(await screen.findByText(/Estás trabajando en/)).toBeInTheDocument()
  })

  it('la pestaña Integraciones muestra la URL del webhook y el token del bot, y guarda la llave de Claude', async () => {
    const api = mockApi({
      'GET /panel/me/': { body: { user: ME.agencyOwner } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
      'GET /panel/bots/': { body: { bots: [BOT], needing_attention: 0 } },
      'GET /panel/bots/b1': { body: { bot: BOT } },
      'GET /panel/bots/b1/claude-key': { body: { key: { configured: false, hint: null, set_at: null } } },
      'PUT /panel/bots/b1/claude-key': { body: { key: { configured: true, hint: 'abcd', set_at: '2026-11-24T00:00:00Z' } } },
      'GET /panel/bots/b1/meta-app': {
        body: { meta_app: { webhook_url: 'https://bots.pixeldc.com/meta/webhook/bt_abc', verify_token: 'pxv_token', has_app_secret: false, app_secret_hint: null, updated_at: '2026-11-24T00:00:00Z' } },
      },
      'GET /panel/bots/b1/sources/': { body: { sources: [] } },
    })
    renderApp('/chatbots/b1')

    await userEvent.click(await screen.findByRole('tab', { name: 'Integraciones' }))
    expect(await screen.findByDisplayValue('https://bots.pixeldc.com/meta/webhook/bt_abc')).toBeInTheDocument()
    expect(screen.getByDisplayValue('pxv_token')).toBeInTheDocument()
    expect(screen.getByText('Falta la clave secreta')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Llave'), 'sk-ant-api03-prueba-abcd')
    await userEvent.click(screen.getAllByRole('button', { name: 'Guardar' })[0])
    await waitFor(() => expect(api.find('PUT', '/panel/bots/b1/claude-key')).toHaveLength(1))
    expect(api.find('PUT', '/panel/bots/b1/claude-key')[0].body).toEqual({ key: 'sk-ant-api03-prueba-abcd' })
  })

  it('la pestaña Canales verifica la conexión con Meta, muestra lo que falla y lo repara sin entrar a Meta', async () => {
    const WABA_CHECK = { id: 'waba', channel_id: 'ch1', title: 'WhatsApp: app suscrita a la cuenta', status: 'error', detail: 'La app no está suscrita a la cuenta de WhatsApp: Meta no envía ningún mensaje.', fixable: true, hint: null }
    const TOKEN_OK = { id: 'token', channel_id: 'ch1', title: 'WhatsApp: token', status: 'ok', detail: null, fixable: false, hint: null }
    const api = mockApi({
      'GET /panel/me/': { body: { user: ME.agencyOwner } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
      'GET /panel/bots/': { body: { bots: [BOT], needing_attention: 0 } },
      'GET /panel/bots/b1': { body: { bot: BOT } },
      'GET /panel/bots/b1/channels': {
        body: { channels: [{ id: 'ch1', kind: 'whatsapp', external_id: '913', display_name: 'WhatsApp', status: 'active', created_at: '2026-11-24T00:00:00Z', account_id: null }] },
      },
      'GET /panel/bots/b1/meta/connection': {
        body: { connection: { webhook_url: 'https://x/meta/webhook/bt_abc', healthy: false, can_repair: true, checks: [TOKEN_OK, WABA_CHECK] } },
      },
      'POST /panel/bots/b1/meta/connection/repair': {
        body: {
          applied: [{ title: 'Suscribir la app a la cuenta de WhatsApp (WhatsApp)', success: true, message: null }],
          connection: { webhook_url: 'https://x/meta/webhook/bt_abc', healthy: true, can_repair: false, checks: [TOKEN_OK, { ...WABA_CHECK, status: 'ok', detail: null, fixable: false }] },
        },
      },
    })
    renderApp('/chatbots/b1')

    await userEvent.click(await screen.findByRole('tab', { name: 'Canales' }))
    expect(await screen.findByText('WABA: sin indicar')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reparar' })).not.toBeInTheDocument() // hasta verificar no se sabe nada

    await userEvent.click(screen.getByRole('button', { name: 'Verificar conexión' }))
    expect(await screen.findByText('Hay puntos por revisar.')).toBeInTheDocument()
    expect(screen.getByText(/Meta no envía ningún mensaje/)).toBeInTheDocument()
    expect(screen.getByText('Se puede reparar')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Reparar' }))
    expect(await screen.findByText(/Todo está en orden/)).toBeInTheDocument()
    expect(screen.getByText(/Suscribir la app a la cuenta de WhatsApp/)).toBeInTheDocument()
    expect(api.find('POST', '/panel/bots/b1/meta/connection/repair')).toHaveLength(1)
    expect(screen.queryByRole('button', { name: 'Reparar' })).not.toBeInTheDocument() // ya no queda nada por reparar
  })

  it('la bandeja muestra un solo hilo por contacto con todo su historial y no deja responder pasadas las 24 horas', async () => {
    const A = 'conv-reciente'
    const OLD = 'conv-antigua'
    mockApi({
      'GET /panel/me/': { body: { user: ME.agencyOwner } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
      'GET /panel/bots/inbox': {
        body: {
          conversations: [
            { id: A, first_message: 'Buenas de nuevo', messages: 4, started_at: '2026-11-20T10:00:00Z', last_message_at: '2026-11-24T10:00:00Z', channel: 'whatsapp', contact: 'Ana (573001112233)', needs_attention: false, human_active: false, can_reply: false, bot_id: 'b1', bot_name: 'Porky', sessions: 2 },
          ],
        },
      },
      ['GET /panel/bots/b1/conversations/' + A]: {
        body: {
          messages: [
            { role: 'user', content: 'Hola, ¿precios?', created_at: '2026-11-20T10:00:00Z', conversation_id: OLD },
            { role: 'assistant', content: 'Claro, te cuento.', created_at: '2026-11-20T10:01:00Z', conversation_id: OLD },
            { role: 'user', content: 'Buenas de nuevo', created_at: '2026-11-24T10:00:00Z', conversation_id: A },
            { role: 'assistant', content: '¡Hola Ana!', created_at: '2026-11-24T10:01:00Z', conversation_id: A },
          ],
        },
      },
    })
    renderApp('/bandeja?bot=b1&c=' + A)

    // Un solo renglón en la lista, que dice cuántas conversaciones tiene el hilo.
    expect((await screen.findAllByText(/2 conversaciones/)).length).toBeGreaterThan(0)
    // El historial completo, de las dos conversaciones, con la separación entre ellas.
    expect(await screen.findByText('Hola, ¿precios?')).toBeInTheDocument()
    expect(screen.getByText('¡Hola Ana!')).toBeInTheDocument()
    expect(screen.getAllByRole('separator', { name: 'Nueva conversación' })).toHaveLength(1)
    // Pasadas las 24 horas se lee todo pero no se puede escribir hasta que el cliente vuelva a escribir.
    expect(screen.queryByLabelText('Tu respuesta')).not.toBeInTheDocument()
    expect(screen.getByText(/pasaron más de 24 horas/)).toBeInTheDocument()
  })

  it('la configuración del chatbot activa la memoria entre conversaciones y guarda cuántos días recuerda', async () => {
    const api = mockApi({
      'GET /panel/me/': { body: { user: ME.agencyOwner } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
      'GET /panel/bots/': { body: { bots: [BOT], needing_attention: 0 } },
      'GET /panel/bots/b1': { body: { bot: BOT } },
      'PATCH /panel/bots/b1': { body: { bot: { ...BOT, remember_past_chats: true, memory_days: 45 } } },
    })
    renderApp('/chatbots/b1')

    const remember = await screen.findByRole('checkbox', { name: /Recordar conversaciones anteriores/ })
    expect(remember).not.toBeChecked() // apagado por defecto
    expect(screen.queryByLabelText(/Recordar hasta/)).not.toBeInTheDocument()

    await userEvent.click(remember)
    await userEvent.type(await screen.findByLabelText(/Recordar hasta/), '45')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() => expect(api.find('PATCH', '/panel/bots/b1')).toHaveLength(1))
    expect(api.find('PATCH', '/panel/bots/b1')[0].body).toMatchObject({ remember_past_chats: true, memory_days: 45 })
  })

  it('la ficha del cliente muestra sus assets con sus chatbots y arranca ilimitado', async () => {
    mockApi({
      'GET /panel/me/': { body: { user: ME.agencyOwner } },
      'GET /panel/clients': { body: { clients: [CLIENT] } },
      'GET /panel/clients/c1': { body: { client: CLIENT } },
      'GET /panel/assets?clientId=c1': { body: { assets: [ASSET] } },
      'GET /panel/bots/': { body: { bots: [BOT], needing_attention: 0 } },
    })
    renderApp('/clientes/c1')

    expect(await screen.findByText('Restaurante')).toBeInTheDocument()
    expect(await screen.findByText('Porky')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Precio y topes' }))
    expect(await screen.findByText(/conversaciones ilimitadas/)).toBeInTheDocument()
  })
})
