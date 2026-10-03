import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { composerAvailableHeight, composerBounds, resizeComposerHeight, composerSizeKey, rememberComposerHeight } from "./composerHeight";

test("sizing binds when the portal DOM mounts, not just when the owner component mounts", () => {
  const source = readFileSync(new URL("./ComposerLayout.tsx", import.meta.url), "utf8");
  assert.match(source, /\[workspace, workspaceRef\] = useState<HTMLDivElement \| null>\(null\)/);
  assert.match(source, /\}, \[workspace\]\)/);
  assert.match(source, /new ResizeObserver\(measure\)/);
  assert.match(source, /new MutationObserver\(changed\)/);
  assert.match(source, /layout\.available <= 0/);
  for (const file of ["main.tsx", "NewSessionWorkspace.tsx"]) {
    const component = readFileSync(new URL(`./${file}`, import.meta.url), "utf8");
    assert.match(component, /ref=\{composer\.workspaceRef\}/);
    assert.match(component, /<ComposerSplitter \{\.\.\.composer\.splitter\}/);
  }
});

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
