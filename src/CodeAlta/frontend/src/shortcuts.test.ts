import test from "node:test";
import assert from "node:assert/strict";
import { resolveShortcut } from "./shortcuts";

test("shortcut map exposes project, navigation, notes, search, settings, and help", () => {
  assert.equal(resolveShortcut({ key: "o", ctrlKey: true }, false, false).action, "openProject");
  assert.equal(resolveShortcut({ key: "ArrowDown", altKey: true }, false, false).action, "nextSession");
  assert.equal(resolveShortcut({ key: "ArrowLeft", altKey: true }, false, false).action, "previousProject");
  assert.equal(resolveShortcut({ key: "n", ctrlKey: true, shiftKey: true }, false, false).action, "toggleNotes");
  assert.equal(resolveShortcut({ key: "f", metaKey: true }, false, false).action, "focusSearch");
  assert.equal(resolveShortcut({ key: ",", ctrlKey: true }, false, false).action, "settings");
  assert.equal(resolveShortcut({ key: "F1" }, false, false).action, "help");
});

test("TUI Ctrl+G chords map to equivalent desktop surfaces", () => {
  const prefix = resolveShortcut({ key: "g", ctrlKey: true }, false, false);
  assert.equal(prefix.chordPending, true);
  assert.equal(resolveShortcut({ key: "p" }, prefix.chordPending, false).action, "focusPrompt");
  assert.equal(resolveShortcut({ key: "r" }, prefix.chordPending, false).action, "providers");
  assert.equal(resolveShortcut({ key: "g" }, prefix.chordPending, false).action, "toggleNotes");
});

test("ordinary shortcuts do not steal editor input but escape and intentional chords work", () => {
  assert.equal(resolveShortcut({ key: "o", ctrlKey: true }, false, true).handled, false);
  assert.equal(resolveShortcut({ key: "Escape" }, false, true).action, "escape");
  assert.equal(resolveShortcut({ key: "g", ctrlKey: true }, false, true).chordPending, true);
});

test("F6 expands from the composer or shell, without stealing IME or handled input", () => {
  for (const editing of [true, false]) {
    assert.equal(resolveShortcut({ key: "F6" }, false, editing).action, "expandPrompt");
    for (const modifier of [{ ctrlKey: true }, { altKey: true }, { shiftKey: true }, { metaKey: true },
      { isComposing: true }, { keyCode: 229 }, { repeat: true }, { defaultPrevented: true }])
      assert.equal(resolveShortcut({ key: "F6", ...modifier }, false, editing).handled, false);
  }
  assert.equal(resolveShortcut({ key: "Escape", isComposing: true }, false, true).handled, false);
});
