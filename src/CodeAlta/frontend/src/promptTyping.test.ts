import assert from "node:assert/strict";
import test from "node:test";
import { keepsTypedKeysSelector, promptTypedText, type PromptTypingKey } from "./promptTyping";

const plain: PromptTypingKey = { key: "a", ctrlKey: false, altKey: false, metaKey: false, isComposing: false, keyCode: 65,
  defaultPrevented: false, repeat: false };
const key = (change: Partial<PromptTypingKey>): PromptTypingKey => ({ ...plain, ...change });
// An element that matches the selectors it is given among the ones listed: what `closest` finds above it.
const within = (...matched: string[]) => ({ closest: (selector: string) => matched.some(value => selector.split(", ").includes(value)) ? {} as Element : null });
const nowhere = within();

test("a character typed where typing goes nowhere is the prompt's", () => {
  assert.equal(promptTypedText(plain, null), "a", "the page itself");
  assert.equal(promptTypedText(plain, nowhere), "a", "a button, a row, the timeline");
  assert.equal(promptTypedText(key({ key: "Z" }), nowhere), "Z", "Shift writes the capital");
  assert.equal(promptTypedText(key({ key: "é" }), nowhere), "é");
  assert.equal(promptTypedText(key({ key: "#" }), nowhere), "#");
  assert.equal(promptTypedText(key({ key: "😀" }), nowhere), "😀", "one character of two code units");
});

test("Ctrl, Alt and the Command key are not redirected to the prompt", () => {
  assert.equal(promptTypedText(key({ key: "@", ctrlKey: true, altKey: true }), nowhere), null);
  assert.equal(promptTypedText(key({ ctrlKey: true }), nowhere), null);
  assert.equal(promptTypedText(key({ altKey: true }), nowhere), null);
  assert.equal(promptTypedText(key({ metaKey: true }), nowhere), null);
});

test("keys that are no character, Space, composed and held keys and keys already used stay where they are", () => {
  for (const name of ["Enter", "Tab", "Escape", "ArrowDown", "Backspace", "F1", "Dead", "Process", "Unidentified", " ", " "])
    assert.equal(promptTypedText(key({ key: name }), nowhere), null, name);
  assert.equal(promptTypedText(key({ isComposing: true }), nowhere), null);
  assert.equal(promptTypedText(key({ keyCode: 229 }), nowhere), null);
  assert.equal(promptTypedText(key({ repeat: true }), nowhere), null);
  assert.equal(promptTypedText(key({ defaultPrevented: true }), nowhere), null, "the F and A of a row of a session, ? that opens Help");
});

test("what writes text, answers letters itself or is a window keeps the keys typed in it", () => {
  for (const place of ["input", "textarea", "select", ".monaco-editor", ".prompt-editor", "[contenteditable]:not([contenteditable='false'])",
    "[data-terminal-keys]", ".terminal-host", ".xterm", "[data-editor-keys]", "[role='menu']", "[role='listbox']", "[role='tree']",
    "[role='grid']", "[role='combobox']", "[role='textbox']", "[role='slider']", "dialog", "[role='dialog']", ".bp6-popover", "iframe"]) {
    assert.ok(keepsTypedKeysSelector.split(", ").includes(place), place);
    assert.equal(promptTypedText(plain, within(place)), null, place);
  }
});
