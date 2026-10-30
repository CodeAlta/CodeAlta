// Test-owned mount of the production prompt editor alone: its formulas, their preview, and the equation editor.
import { createElement, useState } from "react";
import { createRoot } from "react-dom/client";
import { PromptEditor, type PromptInput } from "./PromptEditor";

const state = { value: "Area $\\pi r^2$ here" };
let input: PromptInput | null = null;
function Fixture() {
  const [value, setValue] = useState(state.value);
  state.value = value;
  return createElement(PromptEditor, { ref: (instance: PromptInput | null) => { input = instance; }, value, onChange: setValue, label: "Message", id: "session-prompt" });
}
createRoot(document.getElementById("app")!).render(createElement(Fixture));

type Field = HTMLElement & { value: string };
Object.assign(window, { mathFixture: {
  state,
  ready: () => !!input && !!document.querySelector("#session-prompt .monaco-editor"),
  caret(offset: number) { input!.focus(); input!.setSelectionRange(offset, offset); },
  preview() {
    const node = document.querySelector(".prompt-math-preview");
    if (!node) return null;
    // Seen: what the window shows at the center of the panel is the panel, not the frame of the prompt that would clip it.
    const box = node.getBoundingClientRect();
    const seen = node.contains(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2));
    // Beside: the button that edits the formula is on its right, not under it.
    const formula = node.querySelector(".prompt-math-preview-formula")!.getBoundingClientRect(), button = node.querySelector("button")!.getBoundingClientRect();
    const cell = node.querySelector<HTMLElement>(".prompt-math-preview-formula")!;
    // and no scroll bar stands between them (a vertical one takes width from the box).
    const beside = button.left >= formula.right - 1 && button.top < formula.bottom && cell.offsetWidth === cell.clientWidth;
    return { drawn: !!node.querySelector(".katex"), error: !!node.querySelector(".katex-error"), edit: node.querySelector("button")?.getAttribute("aria-label"), seen, beside };
  },
  tinted: () => document.querySelectorAll("#session-prompt .prompt-math").length,
  edit: () => input!.editEquation(),
  clickEdit: () => document.querySelector<HTMLButtonElement>(".prompt-math-preview-edit")!.click(),
  editor() {
    const dialog = document.querySelector<HTMLDialogElement>("dialog.equation-editor-dialog");
    const field = dialog?.querySelector<Field>("math-field");
    return dialog && { open: dialog.open, title: dialog.querySelector("#equation-editor-title")?.textContent, field: field?.value ?? null,
      source: dialog.querySelector("textarea")?.value, keyboard: (window as unknown as { mathVirtualKeyboard?: { container: Element | null } }).mathVirtualKeyboard?.container === dialog,
      action: Array.from(dialog.querySelectorAll(".equation-editor-actions button")).map(button => button.textContent).at(-1) };
  },
  write(tex: string) { const field = document.querySelector<Field>("dialog.equation-editor-dialog math-field")!; field.value = tex; field.dispatchEvent(new Event("input", { bubbles: true })); },
  display() { Array.from(document.querySelectorAll<HTMLButtonElement>("dialog.equation-editor-dialog .bp6-segmented-control button")).find(button => button.textContent === "Display")!.click(); },
  insert() { Array.from(document.querySelectorAll<HTMLButtonElement>("dialog.equation-editor-dialog .equation-editor-actions button")).at(-1)!.click(); },
} });
