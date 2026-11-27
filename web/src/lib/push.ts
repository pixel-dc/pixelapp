// Notificaciones del navegador (Web Push). El service worker (/sw.js) las muestra aunque el panel esté cerrado; el servidor decide a quién
// llegan: a los usuarios del cliente de la conversación y a la agencia.

import { get, post } from '@/lib/api'

export type PushState =
  /** El navegador no las admite. */
  | 'unsupported'
  /** iPhone o iPad fuera de la app instalada: solo funcionan desde el ícono en la pantalla de inicio. */
  | 'needs-install'
  /** La persona las bloqueó: hay que permitirlas desde los ajustes del navegador o del teléfono. */
  | 'denied'
  | 'off'
  | 'on'

const isIos = () => /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)

const isStandalone = () =>
  window.matchMedia?.('(display-mode: standalone)').matches || (navigator as Navigator & { standalone?: boolean }).standalone === true

export const supportsPush = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window

/** Registra el service worker al abrir el panel (sin él no llegan notificaciones). */
export function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) return
  navigator.serviceWorker.register('/sw.js').catch(() => {
    // Sin service worker el panel funciona igual; solo no hay notificaciones.
  })
}

async function currentSubscription() {
  const registration = await navigator.serviceWorker.ready
  return registration.pushManager.getSubscription()
}

export async function pushState(): Promise<PushState> {
  if (!supportsPush()) return isIos() && !isStandalone() ? 'needs-install' : 'unsupported'
  if (isIos() && !isStandalone()) return 'needs-install'
  if (Notification.permission === 'denied') return 'denied'
  if (Notification.permission !== 'granted') return 'off'
  const subscription = await currentSubscription()
  if (!subscription) return 'off'
  // El servidor guarda a quién avisar: se le recuerda en cada visita (también si en este teléfono entró otra persona).
  await sendToServer(subscription).catch(() => undefined)
  return 'on'
}

function decodeKey(base64url: string) {
  const padded = (base64url + '='.repeat((4 - (base64url.length % 4)) % 4)).replace(/-/g, '+').replace(/_/g, '/')
  return Uint8Array.from(atob(padded), (c) => c.charCodeAt(0))
}

function sendToServer(subscription: PushSubscription) {
  const json = subscription.toJSON()
  return post('/panel/push/subscriptions', { endpoint: subscription.endpoint, p256dh: json.keys?.p256dh, auth: json.keys?.auth })
}

/** Pide permiso (debe llamarse desde un toque del usuario: iPhone lo exige) y suscribe este dispositivo. */
export async function enablePush(): Promise<PushState> {
  if (!supportsPush()) return 'unsupported'
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') return permission === 'denied' ? 'denied' : 'off'
  const registration = await navigator.serviceWorker.ready
  const { public_key } = await get<{ public_key: string }>('/panel/push/key')
  let subscription = await registration.pushManager.getSubscription()
  // Una suscripción creada con otra llave del servidor ya no sirve: se rehace.
  const key = decodeKey(public_key)
  const existing = subscription?.options.applicationServerKey
  if (subscription && existing && !sameKey(new Uint8Array(existing), key)) {
    await subscription.unsubscribe()
    subscription = null
  }
  subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key })
  await sendToServer(subscription)
  return 'on'
}

export async function disablePush(): Promise<PushState> {
  const subscription = await currentSubscription()
  if (subscription) {
    await post('/panel/push/subscriptions/remove', { endpoint: subscription.endpoint }).catch(() => undefined)
    await subscription.unsubscribe()
  }
  return 'off'
}

export async function testPush() {
  const subscription = await currentSubscription()
  if (!subscription) throw new Error('Activa primero las notificaciones en este dispositivo.')
  await post('/panel/push/test', { endpoint: subscription.endpoint })
}

/** Al salir, este dispositivo deja de avisar a esa persona. */
export async function forgetPushOnLogout() {
  if (!supportsPush()) return
  try {
    const subscription = await currentSubscription()
    if (subscription) await post('/panel/push/subscriptions/remove', { endpoint: subscription.endpoint })
  } catch {
    // Si falla, el servidor la reasigna a quien entre después en este teléfono.
  }
}

const sameKey = (a: Uint8Array, b: Uint8Array) => a.length === b.length && a.every((v, i) => v === b[i])
