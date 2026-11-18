import assert from "node:assert/strict";
import test from "node:test";
import { openTabSwitcher, recentOrder, rememberRecent, stepTabSwitcher } from "./recentTabs";

test("the tab shown comes first in the recent tabs, once", () => {
  const open = ["a", "b", "c"];
  assert.deepEqual(rememberRecent(["a", "b"], "c", open), ["c", "a", "b"]);
  assert.deepEqual(rememberRecent(["a", "b", "c"], "c", open), ["c", "a", "b"]);
});

test("replacing previews cannot displace the history of tabs that are still open", () => {
  let recent = ["previous", "oldest"] as readonly string[];
  for (let index = 0; index < 128; index++) {
    const preview = `preview-${index}`;
    recent = rememberRecent(recent, preview, [preview, "oldest", "previous"]);
  }
  assert.deepEqual(recent, ["preview-127", "previous", "oldest"]);
});

test("the tabs are ordered by how recently they were shown, the tabs never shown after them", () => {
  assert.deepEqual(recentOrder(["c", "a"], ["a", "b", "c", "d"], "c"), ["c", "a", "b", "d"]);
  // The one shown is first even when it is not the latest remembered; a tab that is gone is dropped.
  assert.deepEqual(recentOrder(["x", "a", "c"], ["a", "b", "c"], "c"), ["c", "a", "b"]);
  assert.deepEqual(recentOrder([], ["a", "b"], "b"), ["b", "a"]);
});

test("the switcher opens on the previous tab, or the oldest going back, and goes around", () => {
  const order = ["c", "a", "b", "d"];
  assert.equal(openTabSwitcher(["a"], 1), null);
  assert.equal(openTabSwitcher([], -1), null);
  const forward = openTabSwitcher(order, 1)!;
  assert.equal(forward.order[forward.index], "a");
  assert.equal(order[stepTabSwitcher(forward, 1).index], "b");
  assert.equal(order[stepTabSwitcher(stepTabSwitcher(stepTabSwitcher(forward, 1), 1), 1).index], "c");
  assert.equal(order[stepTabSwitcher(forward, -1).index], "c");
  const back = openTabSwitcher(order, -1)!;
  assert.equal(order[back.index], "d");
  assert.equal(order[stepTabSwitcher(back, 1).index], "c");
});

test("two quick Ctrl+Tab presses go back and forth between the last two tabs", () => {
  let recent: readonly string[] = ["a", "b", "c"];
  let current = "a";
  for (const expected of ["b", "a", "b"]) {
    const switcher = openTabSwitcher(recentOrder(recent, ["a", "b", "c"], current), 1)!;
    current = switcher.order[switcher.index];
    assert.equal(current, expected);
    recent = rememberRecent(recent, current, ["a", "b", "c"]);
  }
});
