import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { blueprintPaletteVariables } from "./src/blueprintPalette";

// Blueprint's stylesheet reaches the bundle with its palette literals turned into palette variables, so the
// color schemes (src/colorSchemes.gen.css) restyle every Blueprint component by redefining those variables.
const blueprintPalette = (): Plugin => ({
  name: "codealta:blueprint-palette",
  enforce: "pre",
  transform(code, id) {
    return /[\\/]@blueprintjs[\\/][^?]*\.css(\?|$)/.test(id) ? { code: blueprintPaletteVariables(code), map: null } : null;
  },
});

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
  plugins: [blueprintPalette(), react()],
  server: { fs: { allow: ["..", fileURLToPath(new URL("../../CodeAlta.Tui/Assets/3d.flf", import.meta.url))] }, host: "127.0.0.1", strictPort: true, port: 5173 },
  build: { sourcemap: false, assetsInlineLimit: 0 },
}));
