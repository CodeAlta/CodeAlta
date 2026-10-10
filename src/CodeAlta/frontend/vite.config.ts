import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { blueprintPaletteVariables } from "./src/blueprintPalette";
import { appModuleFile, appModules } from "./src/lent/appModules";
import { importMapText, lentLibraries } from "./src/lent/libraries";
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

// The font of the terminals is redistributed under the SIL Open Font License, which goes where the font goes.
const terminalFontLicense = (): Plugin => ({
  name: "codealta:terminal-font-license",
  generateBundle() {
    this.emitFile({ type: "asset", fileName: "assets/CaskaydiaCoveNerdFont-LICENSE.txt",
      source: readFileSync(fileURLToPath(new URL("./src/terminal/fonts/LICENSE.txt", import.meta.url)), "utf8") });
  },
});

// The libraries lent to plugins (src/lent) are entries of the build of their own, at fixed addresses (lib/<name>.js), that share
// their code with the application's: a module of a plugin that imports them gets the instances the application runs. Their
// exports are kept whole (a build of the application alone drops what its entry does not export), so a library is complete and
// the part the application does not use stays in the file of the library, loaded when a plugin first imports it.
// The import map that gives them their bare names is written in the entry document here, from the same list, so the document cannot
// name what the build does not emit. It is inline: the policy of the page allows it by the hash of its text (`assets.csp` of `neoastra.json`).
const importMapPlace = "<!-- importmap -->";
const lentModules = (): Plugin => ({
  name: "codealta:lent-modules",
  transformIndexHtml: { order: "pre", handler: html => {
    if (!html.includes(importMapPlace)) throw new Error(`index.html has no place for the import map of the lent libraries (${importMapPlace}).`);
    return html.replace(importMapPlace, () => `<script type="importmap">${importMapText()}</script>`);
  } },
  buildStart() {
    for (const library of lentLibraries) {
      this.emitFile({ type: "chunk", id: fileURLToPath(new URL(`./${library.entry}`, import.meta.url)), fileName: library.file, preserveSignature: "strict" });
    }

    // The modules of the application's own build that built-in plugins name (src/lent/appModules.ts).
    for (const module of appModules) {
      this.emitFile({ type: "chunk", id: fileURLToPath(new URL(`./${module.entry}`, import.meta.url)), fileName: appModuleFile(module.name), preserveSignature: "strict" });
    }
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
  plugins: [blueprintPalette(), react(), startupScreen(), terminalFontLicense(), lentModules()],
  server: { fs: { allow: ["..", fileURLToPath(new URL("../../CodeAlta.Tui/Assets/3d.flf", import.meta.url))] }, host: "127.0.0.1", strictPort: true, port: 5173 },
  build: { sourcemap: false, assetsInlineLimit: 0 },
}));
