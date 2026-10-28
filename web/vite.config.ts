import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// En desarrollo, /api se reenvía a la API local. En producción la API sirve estos archivos desde wwwroot (mismo origen).
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': 'http://127.0.0.1:5080' } },
  build: { outDir: '../server/src/Pos.Api/wwwroot', emptyOutDir: true },
})
