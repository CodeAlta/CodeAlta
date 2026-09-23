import assert from "node:assert/strict";
import test from "node:test";
import { dispatchComposerKey } from "./composerKeyboard";

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
