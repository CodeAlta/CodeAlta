import assert from "node:assert/strict";
import test from "node:test";
import { collapsedSessionWidth, constrainPaneLayout, defaultPaneLayout, parsePaneLayout, persistPaneLayout, restorePaneLayout } from "./paneLayout";

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

test("constraining preserves a usable content pane and pane minimums", () => {
  assert.deepEqual(constrainPaneLayout({ projects: 440, sessions: 560 }, 1000), { projects: 284, sessions: 220 });
  assert.deepEqual(constrainPaneLayout({ projects: 400, sessions: 420 }, 1200), { projects: 400, sessions: 304 });
});

test("restoring preferences keeps rail widths across a narrow window and later expansion", () => {
  const saved = '{"projects":400,"sessions":420}';
  const preferred = restorePaneLayout(() => saved, 900);
  assert.deepEqual(preferred, { projects: 400, sessions: 420 });
  assert.deepEqual(constrainPaneLayout(preferred, 900), { projects: 184, sessions: 220 });
  assert.deepEqual(constrainPaneLayout(preferred, 1400), { projects: 400, sessions: 420 });
  assert.deepEqual(parsePaneLayout('{"projects":-1,"sessions":99999}', 1400), { projects: 160, sessions: 560 });
  assert.deepEqual(parsePaneLayout('null', 1400), defaultPaneLayout);
});

test("collapsed project width is retained, while the visible session rail has no ghost project budget", () => {
  const preferred = { projects: 400, sessions: 420 };
  assert.equal(collapsedSessionWidth(preferred, 900), 412);
  assert.equal(collapsedSessionWidth(preferred, 1400), 420);
  assert.deepEqual(constrainPaneLayout(preferred, 1400), preferred);
});
