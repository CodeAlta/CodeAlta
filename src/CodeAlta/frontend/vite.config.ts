import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => ({
  resolve: {
    alias: {
      "#neoastra": fileURLToPath(new URL(
        mode === "demo" ? "./src/demo-api.ts" : "../obj/neoastra/neoastra.ts",
        import.meta.url)),
    },
    dedupe: ["@neoastra/client"],
  },
  define: {
    "import.meta.env.VITE_DEMO_MODE": JSON.stringify(mode === "demo" ? "true" : "false"),
  },
  base: "./",
  plugins: [react()],
  server: { fs: { allow: [".."] }, host: "127.0.0.1", strictPort: true, port: 5173 },
  build: { sourcemap: false, assetsInlineLimit: 0 },
}));
