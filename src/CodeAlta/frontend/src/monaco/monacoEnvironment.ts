import * as monaco from "monaco-editor/editor/editor.api.js";
import "monaco-editor/editor/browser/coreCommands.js";
import "monaco-editor/features/clipboard/register.js";
import "monaco-editor/features/wordOperations/register.js";
import "monaco-editor/features/linesOperations/register.js";
import "monaco-editor/features/find/register.js";
import "monaco-editor/features/contextmenu/register.js";
import "monaco-editor/features/tokenization/register.js";
// What a code editor needs beyond typing: going to a line, folding, matching brackets, several carets, comments,
// indentation, the other places a word is used, links, and suggestions when asked for. Every editor of the page
// gets them: the small ones (the prompt, a configuration) turn off what would show in them.
import "monaco-editor/features/gotoLine/register.js";
import "monaco-editor/features/folding/register.js";
import "monaco-editor/features/bracketMatching/register.js";
import "monaco-editor/features/multicursor/register.js";
import "monaco-editor/features/comment/register.js";
import "monaco-editor/features/indentation/register.js";
import "monaco-editor/features/wordHighlighter/register.js";
import "monaco-editor/features/smartSelect/register.js";
import "monaco-editor/features/caretOperations/register.js";
import "monaco-editor/features/cursorUndo/register.js";
import "monaco-editor/features/lineSelection/register.js";
import "monaco-editor/features/links/register.js";
import "monaco-editor/features/suggest/register.js";
// The icon font of the editor's own widgets: the marks of a diff, the arrows of the find box.
import "monaco-editor/features/codicon/register.js";
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker";
import { shellColor } from "../shellColors";
import { installEditorFocus } from "./editorFocus";

// Vite emits a same-origin worker: no CDN, inline script, eval or blob CSP exception.
globalThis.MonacoEnvironment = Object.freeze({ getWorker: () => new EditorWorker() });
// One editor of the page has the text focus, also when the browser announced no change of focus.
installEditorFocus(monaco);

// Inline code in Markdown stands out in the same red as in the rendered messages (Blueprint red 4 / red 2).
const themes = { dark: { base: "vs-dark", inline: "e76a6e", inserted: "72ca9b", deleted: "fa999c" },
  light: { base: "vs", inline: "ac2f33", inserted: "1c6e42", deleted: "ac2f33" } } as const;
// Added and removed lines of a diff are green and red; the base themes have no rule for them.
const rules = (mode: keyof typeof themes, inline: string) => [{ token: "variable.md", foreground: inline },
  { token: "inserted", foreground: themes[mode].inserted }, { token: "deleted", foreground: themes[mode].deleted }];
// A diff tints a changed line lightly and the changed words in it more, in the green and red of the shell.
const diffColors = (mode: keyof typeof themes): Record<string, string> => {
  const [added, removed] = mode === "dark" ? ["#2ea043", "#f85149"] : ["#1f883d", "#cf222e"];
  return { "diffEditor.insertedLineBackground": `${added}26`, "diffEditor.insertedTextBackground": `${added}59`,
    "diffEditor.removedLineBackground": `${removed}24`, "diffEditor.removedTextBackground": `${removed}59`,
    "diffEditorGutter.insertedLineBackground": `${added}33`, "diffEditorGutter.removedLineBackground": `${removed}33`,
    "diffEditorOverview.insertedForeground": `${added}99`, "diffEditorOverview.removedForeground": `${removed}99`,
    "diffEditor.diagonalFill": mode === "dark" ? "#ffffff14" : "#00000014" };
};
monaco.editor.defineTheme("codealta-dark", { base: themes.dark.base, inherit: true, colors: diffColors("dark"), rules: rules("dark", themes.dark.inline) });
monaco.editor.defineTheme("codealta-light", { base: themes.light.base, inherit: true, colors: diffColors("light"), rules: rules("light", themes.light.inline) });

// A palette tints the editor like the panels around it; Blueprint's own palette keeps Monaco's surfaces.
function applyShellTheme() {
  const root = document.documentElement;
  const mode = root.dataset.theme === "light" ? "light" : "dark";
  if (!root.dataset.palette) { monaco.editor.setTheme(`codealta-${mode}`); return; }
  const probe = root.appendChild(document.createElement("span"));
  const color = (property: string) => shellColor(probe, property);
  const colors: Record<string, string> = diffColors(mode);
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
  set("diffEditor.unchangedRegionBackground", color("--panel"));
  set("diffEditor.unchangedCodeBackground", color("--text"), "0a");
  set("editorWidget.background", color("--panel-2"));
  set("editorWidget.border", color("--text"), "33");
  set("quickInput.background", color("--panel-2"));
  set("quickInput.foreground", color("--text"));
  set("quickInputList.focusBackground", color("--accent"), "40");
  set("editorSuggestWidget.background", color("--panel-2"));
  set("editorSuggestWidget.selectedBackground", color("--accent"), "40");
  set("editorBracketMatch.background", color("--accent"), "26");
  set("editorBracketMatch.border", color("--accent"), "80");
  set("editor.wordHighlightBackground", color("--text"), "1f");
  set("editor.findMatchBackground", color("--accent"), "73");
  set("editor.findMatchHighlightBackground", color("--accent"), "38");
  set("minimap.background", color("--panel"));
  set("input.background", color("--bg"));
  set("scrollbarSlider.background", color("--text"), "26");
  set("scrollbarSlider.hoverBackground", color("--text"), "40");
  set("scrollbarSlider.activeBackground", color("--text"), "59");
  const inline = color("--inline-code-text")?.slice(1) ?? themes[mode].inline;
  probe.remove();
  monaco.editor.defineTheme("codealta-scheme", { base: themes[mode].base, inherit: true, colors, rules: rules(mode, inline) });
  monaco.editor.setTheme("codealta-scheme");
}

/** Keeps Monaco's theme in step with the shell theme and palette; returns the disposer. */
export function followShellTheme(): () => void {
  applyShellTheme();
  const observer = new MutationObserver(applyShellTheme);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-palette"] });
  return () => observer.disconnect();
}

export { monaco };
