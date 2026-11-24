export interface FaqItem {
  question: string
  answer: string
}

const QUESTION = /^\s*(?:P|Q|Pregunta)\s*[:.)\-–]\s*(.*)$/i
const ANSWER = /^\s*(?:R|A|Respuesta)\s*[:.)\-–]\s*(.*)$/i
const HEADING = /^\s*#{2,6}\s+(.*)$/

/**
 * Convierte texto pegado en preguntas y respuestas. Acepta dos formatos:
 *  - «P: …» / «R: …» (también «Pregunta:» / «Respuesta:»), con respuestas de varias líneas;
 *  - Markdown: un título (##, ###…) con la pregunta y las líneas siguientes como respuesta.
 * Las negritas de Markdown de la pregunta se quitan; las preguntas sin respuesta se descartan.
 */
export function parseFaq(text: string): FaqItem[] {
  const items: { question: string; answer: string[] }[] = []
  let current: { question: string; answer: string[] } | null = null
  let inAnswer = false

  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trimEnd()
    const q = QUESTION.exec(line)
    const h = HEADING.exec(line)
    const a = ANSWER.exec(line)
    if (q || h) {
      current = { question: (q ? q[1] : h![1]).replace(/\*\*/g, '').trim(), answer: [] }
      items.push(current)
      inAnswer = false
    } else if (a && current) {
      current.answer.push(a[1])
      inAnswer = true
    } else if (current) {
      if (line.trim().length === 0) {
        if (inAnswer) current.answer.push('')
      } else {
        current.answer.push(line.trim())
        inAnswer = true
      }
    }
  }

  return items
    .map((i) => ({ question: i.question, answer: i.answer.join('\n').replace(/\n{3,}/g, '\n\n').trim() }))
    .filter((i) => i.question.length > 0 && i.answer.length > 0)
}
