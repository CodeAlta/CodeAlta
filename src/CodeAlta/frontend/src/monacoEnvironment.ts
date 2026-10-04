import * as monaco from "monaco-editor/editor/editor.api.js";
import "monaco-editor/editor/browser/coreCommands.js";
import "monaco-editor/features/clipboard/register.js";
import "monaco-editor/features/wordOperations/register.js";
import "monaco-editor/features/linesOperations/register.js";
import "monaco-editor/features/find/register.js";
import "monaco-editor/features/contextmenu/register.js";
import "monaco-editor/features/tokenization/register.js";
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker";
import { shellColor } from "./shellColors";

// Vite emits a same-origin worker: no CDN, inline script, eval or blob CSP exception.
globalThis.MonacoEnvironment = Object.freeze({ getWorker: () => new EditorWorker() });

// Inline code in Markdown stands out in the same red as in the rendered messages (Blueprint red 4 / red 2).
const themes = { dark: { base: "vs-dark", inline: "e76a6e", inserted: "72ca9b", deleted: "fa999c" },
  light: { base: "vs", inline: "ac2f33", inserted: "1c6e42", deleted: "ac2f33" } } as const;
// Added and removed lines of a diff are green and red; the base themes have no rule for them.
const rules = (mode: keyof typeof themes, inline: string) => [{ token: "variable.md", foreground: inline },
  { token: "inserted", foreground: themes[mode].inserted }, { token: "deleted", foreground: themes[mode].deleted }];
monaco.editor.defineTheme("codealta-dark", { base: themes.dark.base, inherit: true, colors: {}, rules: rules("dark", themes.dark.inline) });
monaco.editor.defineTheme("codealta-light", { base: themes.light.base, inherit: true, colors: {}, rules: rules("light", themes.light.inline) });

// A color scheme tints the editor like the panels around it; Blueprint's own palette keeps Monaco's surfaces.
function applyShellTheme() {
  const root = document.documentElement;
  const mode = root.dataset.theme === "light" ? "light" : "dark";
  if (!root.dataset.colorScheme) { monaco.editor.setTheme(`codealta-${mode}`); return; }
  const probe = root.appendChild(document.createElement("span"));
  const color = (property: string) => shellColor(probe, property);
  const colors: Record<string, string> = {};
  const set = (key: string, value: string | undefined, alpha = "") => { if (value) colors[key] = value + alpha; };
  set("editor.background", color("--panel"));
  set("editor.foreground", color("--text"));
  set("editorLineNumber.foreground", color("--muted"), "b0");
  set("editorLineNumber.activeForeground", color("--text"));
  set("editorCursor.foreground", color("--accent"));
  set("editor.selectionBackground", color("--accent"), "55");
  set("editor.inactiveSelectionBackground", color("--accent"), "30");
  set("editor.lineHighlightBackground", color("--text"), "0d");
  set("editor.lineHighlightBorder", color("--text"), "00");
  set("editorIndentGuide.background1", color("--text"), "1a");
  set("editorWidget.background", color("--panel-2"));
  set("editorWidget.border", color("--text"), "33");
  set("input.background", color("--bg"));
  set("scrollbarSlider.background", color("--text"), "26");
  set("scrollbarSlider.hoverBackground", color("--text"), "40");
  set("scrollbarSlider.activeBackground", color("--text"), "59");
  const inline = color("--inline-code-text")?.slice(1) ?? themes[mode].inline;
  probe.remove();
  monaco.editor.defineTheme("codealta-scheme", { base: themes[mode].base, inherit: true, colors, rules: rules(mode, inline) });
  monaco.editor.setTheme("codealta-scheme");
}

/** Keeps Monaco's theme in step with the shell theme and color scheme; returns the disposer. */
export function followShellTheme(): () => void {
  applyShellTheme();
  const observer = new MutationObserver(applyShellTheme);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-color-scheme"] });
  return () => observer.disconnect();
}

export { monaco };
