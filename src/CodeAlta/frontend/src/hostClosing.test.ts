import assert from "node:assert/strict";
import test from "node:test";
import { readHostClosing } from "./hostClosing";

test("a host that closes says what it waits for, and when the page may end it", () => {
  assert.deepEqual(readHostClosing({ closing: { step: "sessions", seconds: 12.7, canForceExit: true } }), { step: "sessions", seconds: 12, canForceExit: true });
  assert.deepEqual(readHostClosing({ closing: { step: null, seconds: 0, canForceExit: false } }), { step: null, seconds: 0, canForceExit: false });
});

test("a host that does not close, or a malformed reply, reads as not closing", () => {
  for (const value of [null, undefined, "x", {}, { closing: null }, { closing: "yes" }, { closing: { seconds: "1", canForceExit: true } },
    { closing: { seconds: -1, canForceExit: true } }, { closing: { seconds: Number.NaN, canForceExit: true } }, { closing: { seconds: 3, canForceExit: "true" } }])
    assert.equal(readHostClosing(value), null);
  // A step that is not a short name is not shown.
  assert.equal(readHostClosing({ closing: { step: "x".repeat(65), seconds: 3, canForceExit: false } })?.step, null);
});
