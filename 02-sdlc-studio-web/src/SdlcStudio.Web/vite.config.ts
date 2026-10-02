import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// During development the React app runs on Vite (http://localhost:5173) and proxies /api to the
// ASP.NET Core API (http://localhost:5180). WHY a proxy instead of CORS-only? Same-origin requests in dev
// mirror production (where the API serves the built UI from wwwroot), so no environment-specific URLs.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5180", changeOrigin: true },
    },
  },
});
