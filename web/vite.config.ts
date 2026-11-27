import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'
import type { ProxyOptions } from 'vite'
import { defineConfig } from 'vitest/config'

// La API decide el cliente por el host (cliente.localhost:5173): el proxy debe reenviarlo tal cual, con su puerto.
const api: ProxyOptions = {
  target: 'http://localhost:5180',
  configure: (proxy) => {
    proxy.on('proxyReq', (req, original) => {
      if (original.headers.host) req.setHeader('host', original.headers.host)
    })
  },
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, './src') },
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    css: false,
    testTimeout: 20000,
    restoreMocks: true,
  },
  server: {
    proxy: { '/panel': api, '/chat': api, '/widget': api, '/widget.js': api, '/meta': api, '/health': api, '/manifest.webmanifest': api, '/app-icon.png': api },
  },
})
