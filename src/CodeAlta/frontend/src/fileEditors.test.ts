import assert from "node:assert/strict";
import test from "node:test";
import { createFileEditors } from "./fileEditors";

test("dirty marks notify subscribers only when they change", () => {
  const editors = createFileEditors();
  let notified = 0;
  const unsubscribe = editors.subscribe(() => { notified++; });
  const before = editors.snapshot();
  editors.setDirty("a", false);
  assert.deepEqual([notified, editors.snapshot()], [0, before]);
  editors.setDirty("a", true);
  editors.setDirty("a", true);
  assert.deepEqual([notified, editors.dirty("a"), editors.dirty("b")], [1, true, false]);
  assert.notEqual(editors.snapshot(), before);
  editors.setDirty("a", false);
  assert.deepEqual([notified, editors.dirty("a")], [2, false]);
  unsubscribe();
  editors.setDirty("a", true);
  assert.equal(notified, 2);
});

test("a mounted editor is saved by key; detaching removes its save and its dirty mark", async () => {
  const editors = createFileEditors();
  assert.equal(await editors.save("a"), false);
  let saves = 0;
  const detach = editors.attach("a", async () => { saves++; return true; });
  editors.attach("failing", async () => false);
  editors.attach("throwing", async () => { throw new Error("bridge closed"); });
  editors.setDirty("a", true);
  assert.equal(await editors.save("a"), true);
  assert.equal(await editors.save("failing"), false);
  assert.equal(await editors.save("throwing"), false);
  assert.equal(saves, 1);
  detach();
  assert.deepEqual([editors.dirty("a"), await editors.save("a"), saves], [false, false, 1]);
});

test("a stale detach does not remove the editor that replaced it", async () => {
  const editors = createFileEditors();
  const first = editors.attach("a", async () => false);
  editors.attach("a", async () => true);
  editors.setDirty("a", true);
  first();
  assert.deepEqual([editors.dirty("a"), await editors.save("a")], [true, true]);
});
