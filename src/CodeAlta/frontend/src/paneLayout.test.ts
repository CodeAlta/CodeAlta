import assert from "node:assert/strict";
import test from "node:test";
import { constrainPaneLayout, defaultPaneLayout, parsePaneLayout, persistPaneLayout, resizePane, restorePaneLayout } from "./paneLayout";

test("restores valid pane widths and rejects malformed desktop state", () => {
  assert.deepEqual(parsePaneLayout('{"projects":300,"sessions":360}', 1400), { projects: 300, sessions: 360 });
  assert.deepEqual(parsePaneLayout("not json", 1400), defaultPaneLayout);
  assert.deepEqual(parsePaneLayout('{"projects":"wide","sessions":300}', 1400), defaultPaneLayout);
  assert.deepEqual(restorePaneLayout(() => { throw new Error("storage unavailable"); }, 1400), defaultPaneLayout);
  assert.equal(persistPaneLayout(() => { throw new Error("storage full"); }, defaultPaneLayout), false);
  let saved = "";
  assert.equal(persistPaneLayout(value => { saved = value; }, { projects: 300, sessions: 360 }), true);
  assert.equal(saved, '{"projects":300,"sessions":360}');
});

test("resizing preserves a usable content pane and pane minimums", () => {
  assert.deepEqual(resizePane(defaultPaneLayout, "projects", 1000, 1200), { projects: 394, sessions: 310 });
  assert.deepEqual(resizePane(defaultPaneLayout, "sessions", -1000, 1200), { projects: 240, sessions: 220 });
  assert.deepEqual(constrainPaneLayout({ projects: 440, sessions: 560 }, 1000), { projects: 284, sessions: 220 });
});
