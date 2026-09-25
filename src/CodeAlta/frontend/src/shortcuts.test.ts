import test from "node:test";
import assert from "node:assert/strict";
import { resolveShortcut, sessionInfoChordContextAllowed, sessionInfoPrefixFromKey } from "./shortcuts";

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

test("Ctrl+G then Ctrl+T opens session info only when available; otherwise browser Ctrl+T remains untouched", () => {
  const prefix = resolveShortcut({ key: "g", ctrlKey: true }, false, true);
  assert.deepEqual(prefix, { action: null, chordPending: true, handled: true });
  assert.equal(sessionInfoPrefixFromKey({ key: "g", ctrlKey: true }, prefix), true);
  assert.equal(sessionInfoPrefixFromKey({ key: "g", metaKey: true }, resolveShortcut({ key: "g", metaKey: true }, false, true)), false);
  for (const modifier of [{ altKey: true }, { shiftKey: true }])
    assert.equal(sessionInfoPrefixFromKey({ key: "g", ctrlKey: true, ...modifier },
      resolveShortcut({ key: "g", ctrlKey: true, ...modifier }, false, true)), false);
  assert.equal(sessionInfoPrefixFromKey({ key: "g", ctrlKey: true, repeat: true },
    resolveShortcut({ key: "g", ctrlKey: true, repeat: true }, false, true)), false);
  const chord = { key: "t", ctrlKey: true };
  assert.deepEqual(resolveShortcut(chord, prefix.chordPending, true, false, true),
    { action: "sessionInfo", chordPending: false, handled: true });
  assert.deepEqual(resolveShortcut(chord, prefix.chordPending, false),
    { action: null, chordPending: false, handled: false });
  assert.equal(resolveShortcut(chord, false, false, false, true).handled, false);
  for (const invalid of [{ key: "t" }, { key: "t", metaKey: true }, { ...chord, metaKey: true },
    { ...chord, altKey: true }, { ...chord, shiftKey: true },
    { ...chord, isComposing: true }, { ...chord, keyCode: 229 }, { ...chord, defaultPrevented: true }, { ...chord, repeat: true }]) {
    const result = resolveShortcut(invalid, true, true, false, true);
    assert.equal(result.handled, false);
    assert.equal(result.chordPending, false);
  }
  assert.equal(resolveShortcut({ key: "g", ctrlKey: true, isComposing: true }, false, true).handled, false);
  assert.equal(resolveShortcut({ key: "g", ctrlKey: true, repeat: true }, false, true).handled, false);
  assert.equal(resolveShortcut({ key: "g", ctrlKey: true, defaultPrevented: true }, false, true).handled, false);
});

test("Ctrl+G then Ctrl+D resolves only the exact eligible reminder chord", () => {
  const prefix = resolveShortcut({ key: "g", ctrlKey: true }, false, true);
  assert.equal(resolveShortcut({ key: "d", ctrlKey: true }, prefix.chordPending, true, false, false, true).action, "reminders");
  for (const invalid of [{ key: "d" }, { key: "d", metaKey: true }, { key: "d", ctrlKey: true, altKey: true },
    { key: "d", ctrlKey: true, isComposing: true }, { key: "d", ctrlKey: true, keyCode: 229 },
    { key: "d", ctrlKey: true, repeat: true }, { key: "d", ctrlKey: true, defaultPrevented: true }])
    assert.equal(resolveShortcut(invalid, true, true, false, false, true).handled, false);
  assert.equal(resolveShortcut({ key: "d", ctrlKey: true }, true, true).handled, false);
});

test("info chord requires workspace focus, a ready trigger, no other modal, and editing only in the prompt", () => {
  const ready = { workspaceActive: true, modalOpen: false, inWorkspace: true,
    editing: false, promptFocused: false, triggerReady: true };
  assert.equal(sessionInfoChordContextAllowed(ready), true);
  assert.equal(sessionInfoChordContextAllowed({ ...ready, editing: true, promptFocused: true }), true);
  for (const blocked of [
    { workspaceActive: false }, { modalOpen: true }, { inWorkspace: false }, { triggerReady: false },
    { editing: true, promptFocused: false },
  ]) {
    assert.equal(sessionInfoChordContextAllowed({ ...ready, ...blocked }), false);
    assert.equal(resolveShortcut({ key: "t", ctrlKey: true }, true, !!blocked.editing, false,
      sessionInfoChordContextAllowed({ ...ready, ...blocked })).handled, false);
  }
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

test("F2 renames only a focused selected project outside editing, IME and modified focus", () => {
  assert.equal(resolveShortcut({ key: "F2" }, false, false, true).action, "renameProject");
  assert.equal(resolveShortcut({ key: "F2" }, false, false).handled, false);
  assert.equal(resolveShortcut({ key: "F2" }, false, true, true).handled, false);
  for (const modifier of [{ ctrlKey: true }, { altKey: true }, { shiftKey: true }, { metaKey: true },
    { isComposing: true }, { keyCode: 229 }, { repeat: true }, { defaultPrevented: true }])
    assert.equal(resolveShortcut({ key: "F2", ...modifier }, false, false, true).handled, false);
});

test("message navigation is retained-window-only; Ctrl+F4 remains unbound", () => {
  const resolve = (key: string, modifiers = {}, editing = false, available = true) =>
    resolveShortcut({ key, ...modifiers }, false, editing, false, false, false, available);
  assert.equal(resolve("F3").action, "messagePrevious");
  assert.equal(resolve("F4").action, "messageNext");
  assert.equal(resolve("F3", { ctrlKey: true }).action, "messageFirst");
  assert.equal(resolve("F4", { ctrlKey: true }).handled, false);
  for (const key of ["F3", "F4"]) {
    assert.equal(resolve(key, {}, true).handled, false);
    assert.equal(resolve(key, {}, false, false).handled, false);
    for (const modifiers of [{ altKey: true }, { shiftKey: true }, { metaKey: true },
      { isComposing: true }, { keyCode: 229 }, { repeat: true }, { defaultPrevented: true }])
      assert.equal(resolve(key, modifiers).handled, false);
  }
});
