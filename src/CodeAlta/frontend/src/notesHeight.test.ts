import assert from "node:assert/strict";
import test from "node:test";
import { defaultNotesHeight, notesResizeKey, persistNotesHeight, resizeNotesHeight, restoreNotesHeight, visibleNotesHeight } from "./notesHeight";

test("notes preferred height survives session changes and viewport constraints", () => {
  let stored: string | null = null;
  const preferred = resizeNotesHeight(defaultNotesHeight, 160);
  assert.equal(persistNotesHeight(value => { stored = value; }, preferred), true);
  const restored = restoreNotesHeight(() => stored);
  assert.equal(restored, preferred);
  assert.equal(visibleNotesHeight(restored, 300), 195);
  assert.equal(visibleNotesHeight(restored, 900), preferred);
  assert.equal(restoreNotesHeight(() => stored), preferred); // Session switches do not reset a window preference.
  assert.equal(resizeNotesHeight(restored, -9999), 112);
  assert.equal(resizeNotesHeight(restored, 9999), 720);
});

test("malformed or inaccessible local state cannot create an invalid notes size", () => {
  for (const raw of ["{}", "null", "true", '"240"', "NaN", "Infinity", "{broken"])
    assert.equal(restoreNotesHeight(() => raw), defaultNotesHeight);
  assert.equal(restoreNotesHeight(() => { throw new Error("storage blocked"); }), defaultNotesHeight);
  assert.equal(persistNotesHeight(() => { throw new Error("storage blocked"); }, 312), false);
  assert.equal(restoreNotesHeight(() => "-100"), 112);
  assert.equal(restoreNotesHeight(() => "10000"), 720);
});

test("horizontal separator keyboard controls change preferred height without swallowing other keys", () => {
  assert.equal(notesResizeKey("ArrowUp"), -16);
  assert.equal(notesResizeKey("ArrowDown"), 16);
  assert.equal(notesResizeKey("Home"), "reset");
  assert.equal(notesResizeKey("Tab"), null);
  assert.equal(notesResizeKey("ArrowLeft"), null);
});
