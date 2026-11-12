// The prompt, an editor of a file and a read-only editor, with their widgets fixed to the window, in a page of the shell's
// theme, under the edit menu of the page: no application host. The test reads the menu a right-click opens, and what its
// entries did, through `editorsFixture`.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { EditMenu } from "./EditMenu";
import { fixtureClipboard, middleOf, readEditMenu } from "./editMenu.probe";
import { monaco } from "./monaco/monacoEnvironment";
import { monacoEditTarget } from "./monaco/monacoEditMenu";
import { PromptEditor } from "./PromptEditor";

document.documentElement.dataset.theme = "dark";
const state = { prompt: "a prompt", clipboard: "" };
const root = document.getElementById("root")!;
const add = (id: string, style: string) => {
  const node = root.appendChild(document.createElement("div"));
  node.id = id; node.style.cssText = style;
  return node;
};
function Prompt() {
  const [value, setValue] = useState(state.prompt);
  return <PromptEditor value={value} label="Prompt" onChange={next => { state.prompt = next; setValue(next); }} />;
}
createRoot(add("prompt", "position: absolute; left: 20px; right: 20px; bottom: 20px;")).render(<Prompt />);
// As the project editor creates the editor of a file.
monaco.editor.create(add("file", "position: absolute; left: 20px; top: 20px; width: 480px; height: 160px;"),
  { value: "a file", ariaLabel: "File", fixedOverflowWidgets: true, minimap: { enabled: false } });
// As a built-in prompt of an agent is shown.
monaco.editor.create(add("readonly", "position: absolute; left: 520px; top: 20px; width: 300px; height: 160px;"),
  { value: "a read-only text", ariaLabel: "Read-only", readOnly: true, fixedOverflowWidgets: true, minimap: { enabled: false } });
createRoot(add("menu", "")).render(<EditMenu clipboard={fixtureClipboard(state)} editors={monacoEditTarget} />);

type Rgba = [number, number, number, number];
const rgba = (color: string): Rgba => {
  const [r = 0, g = 0, b = 0, a = 1] = (color.match(/[\d.]+/g) ?? []).map(Number);
  return [r, g, b, color.startsWith("rgba") ? a : 1];
};
const luminance = ([r, g, b]: Rgba) => {
  const channel = (value: number) => { const c = value / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
};

Object.assign(window, { editorsFixture: {
  state,
  ready: () => document.querySelectorAll(".monaco-editor").length === 3,
  theme(mode: "dark" | "light") { document.documentElement.dataset.theme = mode; },
  /** Where to right-click in an editor: near the start of its first line, in the coordinates of the page. The page has
   * not the style of Monaco, which the harness does not load: its lines are not where its view draws them. */
  point(id: string) {
    const box = document.querySelector(`#${id} .monaco-editor`)!.getBoundingClientRect();
    return { x: Math.round(box.x + 20), y: Math.round(box.y + 10) };
  },
  /** The middle of the entry of the open menu with that text. */
  entry(text: string) {
    const item = Array.from(document.querySelectorAll(".edit-menu [role=menuitem]")).find(element => element.querySelector(".bp6-fill")?.textContent === text);
    return item ? middleOf(item) : null;
  },
  menu: readEditMenu,
  /** The surface of the open menu: its opacity, its luminance, and the contrast of its first enabled entry on it. */
  surface() {
    const surface = document.querySelector<HTMLElement>(".edit-menu .bp6-menu");
    const label = document.querySelector<HTMLElement>('.edit-menu [role=menuitem]:not([aria-disabled="true"])');
    if (!surface || !label) return null;
    const background = rgba(getComputedStyle(surface).backgroundColor), text = rgba(getComputedStyle(label).color);
    const [light, dark] = [luminance(background), luminance(text)].sort((a, b) => b - a);
    return { opacity: background[3], contrast: Math.round((light + 0.05) / (dark + 0.05) * 10) / 10, background: luminance(background) };
  },
} });
