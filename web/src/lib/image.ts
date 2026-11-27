const MAX_BYTES = 200 * 1024

/**
 * Recorta una imagen al cuadrado y la reduce (por defecto a 256 px) antes de subirla: pesa poco, no se deforma y el servidor solo
 * recibe PNG, JPEG o WebP. Devuelve el archivo listo para enviar.
 */
export async function resizeToSquare(file: File, side = 256): Promise<File> {
  if (!/^image\/(png|jpe?g|webp|gif|bmp)$/i.test(file.type)) throw new Error('Usa una imagen PNG, JPG o WebP.')
  const bitmap = await createImageBitmap(file)
  try {
    const crop = Math.min(bitmap.width, bitmap.height)
    const canvas = document.createElement('canvas')
    canvas.width = side
    canvas.height = side
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('Tu navegador no pudo preparar la imagen.')
    ctx.drawImage(bitmap, (bitmap.width - crop) / 2, (bitmap.height - crop) / 2, crop, crop, 0, 0, side, side)

    for (const [type, ext] of [['image/webp', 'webp'], ['image/jpeg', 'jpg']] as const) {
      for (const quality of [0.9, 0.8, 0.65, 0.5]) {
        const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, type, quality))
        // Algunos navegadores no saben escribir WebP y devuelven PNG: solo vale si es el tipo pedido.
        if (blob && blob.type === type && blob.size <= MAX_BYTES) return new File([blob], `imagen.${ext}`, { type })
      }
    }

    throw new Error('La imagen es demasiado pesada. Prueba con otra.')
  } finally {
    bitmap.close()
  }
}

/**
 * Icono de la app instalable de un cliente: el logo entero (sin recortar) centrado sobre un fondo sólido, en PNG cuadrado. El fondo evita que
 * iPhone rellene de negro lo transparente; el margen, que Android recorte el logo en su máscara redonda.
 */
export async function appIconPng(file: File, background = '#ffffff'): Promise<File> {
  if (!/^image\/(png|jpe?g|webp|gif|bmp)$/i.test(file.type)) throw new Error('Usa una imagen PNG, JPG o WebP.')
  const bitmap = await createImageBitmap(file)
  try {
    for (const side of [512, 384, 256]) {
      const canvas = document.createElement('canvas')
      canvas.width = side
      canvas.height = side
      const ctx = canvas.getContext('2d')
      if (!ctx) throw new Error('Tu navegador no pudo preparar la imagen.')
      ctx.fillStyle = background
      ctx.fillRect(0, 0, side, side)
      const box = side * 0.8
      const scale = Math.min(box / bitmap.width, box / bitmap.height)
      const w = bitmap.width * scale
      const h = bitmap.height * scale
      ctx.imageSmoothingQuality = 'high'
      ctx.drawImage(bitmap, (side - w) / 2, (side - h) / 2, w, h)
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'))
      if (blob && blob.size <= MAX_BYTES) return new File([blob], 'icono.png', { type: 'image/png' })
    }
    throw new Error('La imagen es demasiado pesada. Prueba con otra más sencilla.')
  } finally {
    bitmap.close()
  }
}
