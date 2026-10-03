import * as monaco from "monaco-editor/editor/editor.api.js";
import "monaco-editor/editor/browser/coreCommands.js";
import "monaco-editor/features/clipboard/register.js";
import "monaco-editor/features/wordOperations/register.js";
import "monaco-editor/features/linesOperations/register.js";
import "monaco-editor/features/find/register.js";
import "monaco-editor/features/contextmenu/register.js";
import "monaco-editor/features/tokenization/register.js";
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker";

// Vite emits a same-origin worker: no CDN, inline script, eval or blob CSP exception.
globalThis.MonacoEnvironment = Object.freeze({ getWorker: () => new EditorWorker() });

// Inline code in Markdown stands out in the same red as in the rendered messages (Blueprint red 4 / red 2).
monaco.editor.defineTheme("codealta-dark", { base: "vs-dark", inherit: true, colors: {}, rules: [{ token: "variable.md", foreground: "e76a6e" }] });
monaco.editor.defineTheme("codealta-light", { base: "vs", inherit: true, colors: {}, rules: [{ token: "variable.md", foreground: "ac2f33" }] });

/** Keeps Monaco's theme in step with the shell theme; returns the disposer. */
export function followShellTheme(): () => void {
  const apply = () => monaco.editor.setTheme(document.documentElement.dataset.theme === "light" ? "codealta-light" : "codealta-dark");
  apply();
  const observer = new MutationObserver(apply);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  return () => observer.disconnect();
}

export { monaco };
