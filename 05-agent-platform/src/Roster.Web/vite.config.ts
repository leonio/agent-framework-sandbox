import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// Where the API is. Under Aspire, `WithReference(api)` passes its address as API_HTTP (and as service discovery
// configuration); outside Aspire it is the API's launch profile port.
const apiTarget = process.env.API_HTTP ?? process.env.services__api__http__0 ?? 'http://localhost:5280';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // Aspire's AddViteApp hands the port to use in PORT. 5173 is what the Keycloak realm's redirect URIs name, and the
    // AppHost pins the browser-facing endpoint there.
    port: Number(process.env.PORT ?? 5173),
    strictPort: true,
    proxy: {
      // Everything under /api goes to the API, including sign-in (/api/auth/login, the OIDC callback) and the
      // server-sent events stream. changeOrigin stays false so the API sees the browser's Host header
      // (localhost:5173): it builds the OIDC redirect URI from it, and the session cookie lands on this origin.
      '/api': { target: apiTarget, changeOrigin: false },
      '/openapi': { target: apiTarget, changeOrigin: false },
    },
  },
});
