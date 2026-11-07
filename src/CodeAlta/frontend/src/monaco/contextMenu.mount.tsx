// The prompt and an editor of a file, both with their widgets fixed to the window, in a page of the shell's theme:
// no application host. The page reads the colors of the menu a right-click opens through `contextMenuFixture`.
import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { PromptEditor } from "../PromptEditor";
import { monaco } from "./monacoEnvironment";

document.documentElement.dataset.theme = "dark";
const app = document.getElementById("app")!;
const add = (id: string, style: string) => {
  const node = app.appendChild(document.createElement("div"));
  node.id = id; node.style.cssText = style;
  return node;
};
createRoot(add("prompt", "position: absolute; left: 20px; right: 20px; bottom: 20px;"))
  .render(createElement(PromptEditor, { value: "a prompt", onChange: () => { }, label: "Prompt" }));
// As the project editor creates the editor of a file.
monaco.editor.create(add("file", "position: absolute; left: 20px; top: 20px; width: 480px; height: 160px;"),
  { value: "a file", ariaLabel: "File", fixedOverflowWidgets: true, minimap: { enabled: false } });

type Rgba = [number, number, number, number];
const rgba = (color: string): Rgba => {
  const [r = 0, g = 0, b = 0, a = 1] = (color.match(/[\d.]+/g) ?? []).map(Number);
  return [r, g, b, color.startsWith("rgba") ? a : 1];
};
const luminance = ([r, g, b]: Rgba) => {
  const channel = (value: number) => { const c = value / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
};

Object.assign(window, { contextMenuFixture: {
  ready: () => document.querySelectorAll(".monaco-editor").length === 2,
  theme(mode: "dark" | "light") { document.documentElement.dataset.theme = mode; },
  /** Where to right-click in an editor: a point of its text, in the coordinates of the page. */
  point(id: string) {
    const box = document.querySelector(`#${id} .view-line`)!.getBoundingClientRect();
    return { x: Math.round(box.x + 20), y: Math.round(box.y + box.height / 2) };
  },
  /** The surface of the open menu and its entries, or null when no menu is open. */
  menu() {
    for (const host of Array.from(document.querySelectorAll<HTMLElement>(".shadow-root-host"))) {
      const surface = host.shadowRoot?.querySelector<HTMLElement>(".monaco-menu-container > .monaco-scrollable-element");
      const labels = Array.from(host.shadowRoot?.querySelectorAll<HTMLElement>(".action-label:not(.separator)") ?? []).filter(label => label.textContent);
      if (!surface || !labels.length || surface.getBoundingClientRect().width === 0) continue;
      const background = rgba(getComputedStyle(surface).backgroundColor), text = rgba(getComputedStyle(labels[0]).color);
      const [light, dark] = [luminance(background), luminance(text)].sort((a, b) => b - a);
      return { entries: labels.map(label => label.textContent), opacity: background[3], contrast: Math.round((light + 0.05) / (dark + 0.05) * 10) / 10,
        background: luminance(background) };
    }
    return null;
  },
} });
