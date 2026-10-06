import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { blueprintPaletteVariables } from "./src/blueprintPalette";
import { splashDocument, splashMarkup, splashScript } from "./src/splashMarkup";

// Blueprint's stylesheet reaches the bundle with its palette literals turned into palette variables, so a
// color scheme (src/colorSchemes.ts) restyles every Blueprint component by redefining those variables.
const blueprintPalette = (): Plugin => ({
  name: "codealta:blueprint-palette",
  enforce: "pre",
  transform(code, id) {
    return /[\\/]@blueprintjs[\\/][^?]*\.css(\?|$)/.test(id) ? { code: blueprintPaletteVariables(code), map: null } : null;
  },
});

// The start-up screen is part of the entry document, where it shows until the application is ready, and a
// document of its own (splash.html) that the host shows while it starts. Its script is a file: the page's
// content security policy allows no inline script.
const startupScreen = (): Plugin => {
  const logo = () => readFileSync(fileURLToPath(new URL("../../../img/CodeAlta.svg", import.meta.url)), "utf8");
  return {
    name: "codealta:startup-screen",
    transformIndexHtml: { order: "post", handler: html => html
      .replace("</head>", `<script src="./splash.js"></script></head>`)
      .replace(`<div id="root">`, `${splashMarkup(logo())}<div id="root">`) },
    generateBundle() {
      this.emitFile({ type: "asset", fileName: "splash.js", source: splashScript });
      this.emitFile({ type: "asset", fileName: "splash.html", source: splashDocument(logo()) });
    },
  };
};

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
  plugins: [blueprintPalette(), react(), startupScreen()],
  server: { fs: { allow: ["..", fileURLToPath(new URL("../../CodeAlta.Tui/Assets/3d.flf", import.meta.url))] }, host: "127.0.0.1", strictPort: true, port: 5173 },
  build: { sourcemap: false, assetsInlineLimit: 0 },
}));
