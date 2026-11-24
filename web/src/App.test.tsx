import { render, screen } from '@testing-library/react'
import { expect, it } from 'vitest'
import App from './App'

it('muestra la marca de Pixela', () => {
  render(<App />)
  expect(screen.getByRole('heading', { name: 'Pixela' })).toBeInTheDocument()
})
