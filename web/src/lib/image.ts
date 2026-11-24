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
