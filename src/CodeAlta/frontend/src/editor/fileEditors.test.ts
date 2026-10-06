import assert from "node:assert/strict";
import test from "node:test";
import { createFileEditors } from "./fileEditors";

test("unsaved files notify subscribers only when they change", () => {
  const editors = createFileEditors();
  let notified = 0;
  const unsubscribe = editors.subscribe(() => { notified++; });
  const before = editors.snapshot();
  editors.setUnsaved("a", []);
  assert.deepEqual([notified, editors.snapshot(), editors.anyDirty()], [0, before, false]);
  editors.setUnsaved("a", ["one.ts"]);
  editors.setUnsaved("a", ["one.ts"]);
  assert.deepEqual([notified, editors.dirty("a"), editors.dirty("b"), editors.anyDirty()], [1, true, false, true]);
  assert.deepEqual([editors.unsaved("a"), editors.unsaved("b")], [["one.ts"], []]);
  assert.notEqual(editors.snapshot(), before);
  editors.setUnsaved("a", ["one.ts", "two.ts"]);
  assert.deepEqual([notified, editors.unsaved("a")], [2, ["one.ts", "two.ts"]]);
  editors.setUnsaved("a", []);
  assert.deepEqual([notified, editors.dirty("a"), editors.anyDirty()], [3, false, false]);
  unsubscribe();
  editors.setUnsaved("a", ["one.ts"]);
  assert.equal(notified, 3);
});

test("a mounted editor is saved by key; detaching removes its actions and its unsaved files", async () => {
  const editors = createFileEditors();
  assert.equal(await editors.save("a"), false);
  let saves = 0;
  const detach = editors.attach("a", { save: async () => { saves++; return true; } });
  editors.attach("failing", { save: async () => false });
  editors.attach("throwing", { save: async () => { throw new Error("bridge closed"); } });
  editors.setUnsaved("a", ["one.ts"]);
  assert.equal(await editors.save("a"), true);
  assert.equal(await editors.save("failing"), false);
  assert.equal(await editors.save("throwing"), false);
  assert.equal(saves, 1);
  detach();
  assert.deepEqual([editors.dirty("a"), await editors.save("a"), saves], [false, false, 1]);
});

test("an editor closes the file it shows; one without a file, or that is gone, closes nothing", () => {
  const editors = createFileEditors();
  let open = 2;
  const detach = editors.attach("a", { save: async () => true, closeFile: () => open > 0 && open-- > 0 });
  editors.attach("plain", { save: async () => true });
  editors.attach("throwing", { save: async () => true, closeFile: () => { throw new Error("gone"); } });
  assert.deepEqual([editors.closeFile("a"), editors.closeFile("a"), editors.closeFile("a"), open], [true, true, false, 0]);
  assert.deepEqual([editors.closeFile("plain"), editors.closeFile("throwing"), editors.closeFile("missing")], [false, false, false]);
  open = 1;
  detach();
  assert.deepEqual([editors.closeFile("a"), open], [false, 1]);
});

test("a stale detach does not remove the editor that replaced it", async () => {
  const editors = createFileEditors();
  const first = editors.attach("a", { save: async () => false });
  editors.attach("a", { save: async () => true });
  editors.setUnsaved("a", ["one.ts"]);
  first();
  assert.deepEqual([editors.dirty("a"), await editors.save("a")], [true, true]);
});
