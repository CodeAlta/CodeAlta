import assert from "node:assert/strict";
import test from "node:test";
import { composerAvailableHeight, composerBounds, composerMinimum, promptEditorMinimum, resizeComposerHeight, composerSizeKey, rememberComposerHeight } from "./composerHeight";

test("composer space is viewport-based and does not shrink as its splitter moves", () => {
  const available = composerAvailableHeight(600, 4, 8);
  assert.equal(available, 588);
  const bounds = composerBounds(available);
  const enlarged = resizeComposerHeight(200, 100, bounds);
  assert.equal(enlarged, 300);
  assert.equal(resizeComposerHeight(enlarged, -100, bounds), 200);
  assert.equal(composerAvailableHeight(180, 4, 24), 152);
  assert.equal(composerAvailableHeight(0, 4, 8), 0);
});

test("composer reservation fits short viewports without destroying the requested height", () => {
  assert.deepEqual(composerBounds(420), { min: 120, max: 324 });
  assert.deepEqual(composerBounds(95), { min: 60, max: 67 });
  assert.equal(resizeComposerHeight(300, 100, composerBounds(420)), 324);
  assert.equal(resizeComposerHeight(300, 100, composerBounds(95)), 67);
  assert.equal(resizeComposerHeight(67, -20, composerBounds(420)), 120);
});

test("the smallest layout of the composer keeps everything but the editor, which keeps two lines", () => {
  // A region of 400px holding a picture strip of 58px and a prompt of 334px, whose editor is 248px high.
  assert.deepEqual(composerMinimum(400, 334, 248), { surface: 150, region: 216 });
  assert.equal(promptEditorMinimum, 64);
  // An editor already at its least height gives nothing back.
  assert.deepEqual(composerMinimum(158, 150, 64), { surface: 150, region: 158 });
});

test("the composer cannot be made smaller than its smallest layout, which comes before the timeline's reserve", () => {
  assert.deepEqual(composerBounds(420, 216), { min: 216, max: 324 });
  assert.deepEqual(composerBounds(420, 80), { min: 120, max: 324 });
  // A short window: the prompt keeps its toolbar and the timeline gives its reserve back.
  assert.deepEqual(composerBounds(250, 216), { min: 216, max: 216 });
  // Shorter than the smallest layout: all the space, and the region scrolls.
  assert.deepEqual(composerBounds(150, 216), { min: 150, max: 150 });
  assert.equal(resizeComposerHeight(250, -400, composerBounds(420, 216)), 216);
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
