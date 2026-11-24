import { describe, expect, it } from 'vitest'
import { parseFaq } from './faq-parse'

describe('parseFaq', () => {
  it('lee el formato P: / R: con respuestas de varias líneas', () => {
    const items = parseFaq('P: ¿Cuál es el horario?\nR: Viernes a domingo.\nDe 11 a 6.\n\nP: ¿Hay parqueadero?\nR: Sí.')
    expect(items).toEqual([
      { question: '¿Cuál es el horario?', answer: 'Viernes a domingo.\nDe 11 a 6.' },
      { question: '¿Hay parqueadero?', answer: 'Sí.' },
    ])
  })

  it('lee el formato Markdown con títulos', () => {
    const items = parseFaq('### ¿Qué es Don Porko?\nUn restaurante campestre.\n\n### **¿Hay animales?**\nSí, hay granja.')
    expect(items).toEqual([
      { question: '¿Qué es Don Porko?', answer: 'Un restaurante campestre.' },
      { question: '¿Hay animales?', answer: 'Sí, hay granja.' },
    ])
  })

  it('acepta Pregunta: / Respuesta: y descarta las preguntas sin respuesta', () => {
    const items = parseFaq('Pregunta: ¿Aceptan tarjeta?\nRespuesta: Lo confirmamos con el equipo.\nPregunta: ¿Sin respuesta?')
    expect(items).toEqual([{ question: '¿Aceptan tarjeta?', answer: 'Lo confirmamos con el equipo.' }])
  })

  it('devuelve una lista vacía con texto sin preguntas', () => {
    expect(parseFaq('solo un párrafo cualquiera')).toEqual([])
  })
})
