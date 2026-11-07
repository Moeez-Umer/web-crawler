import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": {
        target: "http://127.0.0.1:5000",
        changeOrigin: true,
        configure: (proxy) => {
          proxy.on("error", (_err, _req, res) => {
            if ("writeHead" in res && !res.headersSent) {
              res.writeHead(502, { "Content-Type": "text/plain" });
            }
            res.end("Bad Gateway");
          });
          proxy.on("proxyRes", (proxyRes) => {
            // On macOS, when the Docker api container is stopped, port 5000 falls back
            // to ControlCenter (AirPlay Receiver) on *:5000 which returns 403 (Server: AirTunes).
            // Map AirTunes responses to 502 Bad Gateway so the UI reports the server as unreachable.
            const serverHeader = String(proxyRes.headers["server"] ?? "");
            if (serverHeader.startsWith("AirTunes")) {
              proxyRes.statusCode = 502;
              proxyRes.statusMessage = "Bad Gateway";
            }
          });
        },
      },
    },
  },
});
