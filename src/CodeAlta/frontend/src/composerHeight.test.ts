import assert from "node:assert/strict";
import test from "node:test";
import { composerBounds, resizeComposerHeight, composerSizeKey, rememberComposerHeight } from "./composerHeight";

test("composer reservation fits short viewports without destroying the requested height", () => {
  assert.deepEqual(composerBounds(420), { min: 120, max: 324 });
  assert.deepEqual(composerBounds(95), { min: 60, max: 67 });
  assert.equal(resizeComposerHeight(300, 100, composerBounds(420)), 324);
  assert.equal(resizeComposerHeight(300, 100, composerBounds(95)), 67);
  assert.equal(resizeComposerHeight(67, -20, composerBounds(420)), 120);
});

test("composer preferences are bounded, selection- and owner-scoped and reset to automatic", () => {
  const key = composerSizeKey("epoch", "project", "one");
  const other = composerSizeKey("epoch", "project", "two");
  const catalog = composerSizeKey(null, "project", "one");
  let sizes = rememberComposerHeight(new Map(), key, 320);
  sizes = rememberComposerHeight(sizes, other, 200);
  assert.equal(sizes.get(key), 320);
  assert.equal(sizes.get(other), 200);
  assert.equal(sizes.get(catalog), undefined);
  sizes = rememberComposerHeight(sizes, key, undefined);
  assert.equal(sizes.has(key), false);
  for (let i = 0; i < 70; i++) sizes = rememberComposerHeight(sizes, composerSizeKey("epoch", "project", String(i)), 150);
  assert.equal(sizes.size, 64);
});
