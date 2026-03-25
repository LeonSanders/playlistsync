import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Dev only: proxy API calls to the local backend
    proxy: {
      '/api':  { target: 'http://localhost:5001', changeOrigin: true, secure: false },
      '/auth': { target: 'http://localhost:5001', changeOrigin: true, secure: false },
    }
  }
})
