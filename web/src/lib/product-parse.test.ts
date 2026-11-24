import { describe, expect, it } from 'vitest'
import { isHttpsImage, parseProducts } from './product-parse'

describe('parseProducts', () => {
  it('lee filas pegadas desde una hoja de cálculo (tabuladores) e ignora el encabezado', () => {
    const text = 'Nombre\tPrecio\tDescripción\tImagen\tCategoría\nBandeja paisa\t$38.000\tFrijoles y chicharrón\thttps://cdn.donporko.co/b.jpg\tPlatos fuertes\nLimonada\t$12.000\t\t\tBebidas'

    expect(parseProducts(text)).toEqual([
      { name: 'Bandeja paisa', price: '$38.000', description: 'Frijoles y chicharrón', image_url: 'https://cdn.donporko.co/b.jpg', category: 'Platos fuertes' },
      { name: 'Limonada', price: '$12.000', description: '', image_url: '', category: 'Bebidas' },
    ])
  })

  it('acepta «|» o «;» y encuentra la foto en cualquier columna', () => {
    expect(parseProducts('Chicharrón | $25.000 | https://x.co/c.jpg | Crocante')).toEqual([
      { name: 'Chicharrón', price: '$25.000', description: 'Crocante', image_url: 'https://x.co/c.jpg', category: '' },
    ])
    expect(parseProducts('Arepa; $4.000')[0]).toMatchObject({ name: 'Arepa', price: '$4.000' })
  })

  it('descarta líneas vacías y filas sin nombre', () => {
    expect(parseProducts('\n\n|$5.000\n')).toEqual([])
  })
})

describe('isHttpsImage', () => {
  it('solo acepta https sin espacios ni usuario', () => {
    expect(isHttpsImage('https://x.co/a.jpg')).toBe(true)
    expect(isHttpsImage('http://x.co/a.jpg')).toBe(false)
    expect(isHttpsImage('https://x.co/a b.jpg')).toBe(false)
    expect(isHttpsImage('https://user:pass@x.co/a.jpg')).toBe(false)
  })
})
