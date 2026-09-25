import assert from "node:assert/strict";
import test from "node:test";
import { dispatchComposerKey, dispatchExpandedComposerKey, dispatchTransientComposerKey } from "./composerKeyboard";

test("regular Enter sends; Ctrl+Enter steers without sending", () => {
  const actions: string[] = [];
  const send = () => actions.push("send");
  const steer = () => actions.push("steer");
  assert.equal(dispatchComposerKey({ key: "Enter" }, send, steer), true);
  assert.equal(dispatchComposerKey({ key: "Enter", ctrlKey: true }, send, steer), true);
  assert.deepEqual(actions, ["send", "steer"]);
});

test("IME confirmation, modified newlines, held keys and handled input never dispatch", () => {
  const unexpected = () => assert.fail("Input must not submit or steer");
  for (const event of [
    { key: "Enter", isComposing: true }, { key: "Enter", keyCode: 229 },
    { key: "Enter", ctrlKey: true, isComposing: true }, { key: "Enter", shiftKey: true },
    { key: "Enter", shiftKey: true, ctrlKey: true }, { key: "Enter", altKey: true },
    { key: "Enter", metaKey: true }, { key: "Enter", repeat: true },
    { key: "Enter", defaultPrevented: true }, { key: "a" },
  ]) assert.equal(dispatchComposerKey(event, unexpected, unexpected), false);
});

test("expanded editor Enter, Escape and Ctrl+Enter only close; editing gestures remain input", () => {
  let closed = 0;
  for (const event of [{ key: "Enter" }, { key: "Escape" }, { key: "Enter", ctrlKey: true }])
    assert.equal(dispatchExpandedComposerKey(event, () => closed++), true);
  assert.equal(closed, 3);
  for (const event of [{ key: "Enter", shiftKey: true }, { key: "Enter", isComposing: true },
    { key: "Escape", isComposing: true }, { key: "Enter", keyCode: 229 }, { key: "Enter", repeat: true },
    { key: "Enter", altKey: true }, { key: "Enter", metaKey: true }, { key: "a" }])
    assert.equal(dispatchExpandedComposerKey(event, () => assert.fail("Must remain editing")), false);
});

test("empty regular prompt consumes only unhandled single-key transient gestures", () => {
  const actions: string[] = [];
  const help = () => actions.push("help");
  const palette = () => actions.push("palette");
  const input = { value: "", selectionStart: 0, selectionEnd: 0 } as HTMLTextAreaElement;
  assert.equal(dispatchTransientComposerKey({ key: "?", shiftKey: true }, input, help, palette), true);
  assert.equal(dispatchTransientComposerKey({ key: "/" }, input, help, palette), true);
  assert.deepEqual(actions, ["help", "palette"]);
  for (const event of [
    { key: "/", isComposing: true }, { key: "?", keyCode: 229 }, { key: "/", repeat: true },
    { key: "/", defaultPrevented: true }, { key: "/", ctrlKey: true }, { key: "/", metaKey: true },
    { key: "?", altKey: true }, { key: "//" }, { key: "Enter" },
  ]) assert.equal(dispatchTransientComposerKey(event, input, help, palette), false);
  assert.equal(dispatchTransientComposerKey({ key: "/" }, input), false);
  for (const changed of [{ value: " ", selectionStart: 0, selectionEnd: 0 },
    { value: "x", selectionStart: 1, selectionEnd: 1 }, { value: "x", selectionStart: 0, selectionEnd: 1 },
    { value: "", selectionStart: 1, selectionEnd: 1 }])
    assert.equal(dispatchTransientComposerKey({ key: "/" }, changed as HTMLTextAreaElement, help, palette), false);
  assert.deepEqual(actions, ["help", "palette"]);
});
