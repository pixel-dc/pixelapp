export interface ProductRow {
  name: string
  price: string
  description: string
  image_url: string
  category: string
}

export const EMPTY_PRODUCT: ProductRow = { name: '', price: '', description: '', image_url: '', category: '' }

const IS_URL = /^https?:\/\/\S+$/i
const HEADER = /^(nombre|producto|plato|name)$/i

/** Una dirección de foto que el cliente podrá abrir: https, sin espacios ni datos de acceso. */
export function isHttpsImage(value: string): boolean {
  const v = value.trim()
  if (!/^https:\/\/\S+$/i.test(v)) return false
  try {
    return new URL(v).username === ''
  } catch {
    return false
  }
}

/**
 * Convierte filas pegadas desde una hoja de cálculo (separadas por tabulador) o escritas con «|» o «;» en productos.
 * Orden: nombre, precio, descripción, categoría; la dirección de la foto se reconoce donde esté (empieza por http).
 * La primera fila se ignora si es un encabezado («Nombre», «Producto», «Plato»).
 */
export function parseProducts(text: string): ProductRow[] {
  const rows: ProductRow[] = []
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (!line) continue
    const cells = (line.includes('\t') ? line.split('\t') : line.includes('|') ? line.split('|') : line.split(';')).map((c) => c.trim())
    if (rows.length === 0 && HEADER.test(cells[0] ?? '')) continue
    const urlAt = cells.findIndex((c) => IS_URL.test(c))
    const image = urlAt >= 0 ? cells[urlAt] : ''
    // Una fila de hoja de cálculo con cinco columnas trae la imagen en la cuarta aunque esté vacía: no debe desplazar la categoría.
    const imageAt = urlAt >= 0 ? urlAt : cells.length >= 5 ? 3 : -1
    const rest = cells.filter((_, i) => i !== imageAt)
    const [name = '', price = '', description = '', category = ''] = rest
    if (!name) continue
    rows.push({ name, price, description, image_url: image, category })
  }
  return rows
}
