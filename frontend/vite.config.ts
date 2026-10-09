import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // The in-app Help page imports docs/How-To-Build-Workflows.md from the
    // repository root (one source of truth for the guide).
    fs: { allow: ['..'] },
    proxy: {
      '/api': 'http://localhost:8000',
      '/webhook': 'http://localhost:8000',
      '/demo': 'http://localhost:8000',
    },
  },
})
