import assert from "node:assert/strict";
import test from "node:test";
import { projectRailProjection } from "./projectRail";
import { focusVisibleProject, persistProjectRailCollapsed, projectRailVisible, resetNarrowRail, restoreProjectRailCollapsed, restoreProjectRailFocus, toggleProjectRail } from "./projectRailVisibility";
import type { WorkspaceSnapshot } from "#neoastra";

test("desktop collapse/reopen and narrow reveal do not change selection or sort", () => {
  let state = { desktopCollapsed: false, narrowOpen: false };
  const selected = { projectId: "p", sessionId: "s", sort: "recent" as const };
  const snapshot: WorkspaceSnapshot = {
    configured: true, projects: [{ id: "p", name: "P", path: "/repo/p", archived: false }], sessions: [],
    projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  };
  const before = projectRailProjection(snapshot, selected.sort);
  assert.equal(projectRailVisible(state, false), true);
  state = toggleProjectRail(state, false);
  assert.equal(projectRailVisible(state, false), false);
  state = toggleProjectRail(state, false);
  assert.equal(projectRailVisible(state, false), true);
  // A viewport change hides the project rail until intentionally opened, without persisting the narrow state.
  state = resetNarrowRail(state);
  assert.equal(projectRailVisible(state, true), false);
  state = toggleProjectRail(state, true);
  assert.equal(projectRailVisible(state, true), true);
  assert.equal(state.desktopCollapsed, false);
  assert.deepEqual(projectRailProjection(snapshot, selected.sort), before);
  assert.deepEqual(selected, { projectId: "p", sessionId: "s", sort: "recent" });
  assert.equal(projectRailVisible(resetNarrowRail(state), true), false);
  assert.equal(projectRailVisible(resetNarrowRail(state), false), true);
});

test("narrow opening never overrides a persisted desktop collapse", () => {
  let state = { desktopCollapsed: restoreProjectRailCollapsed(() => "collapsed"), narrowOpen: false };
  assert.equal(projectRailVisible(state, false), false);
  state = toggleProjectRail(state, true);
  assert.equal(projectRailVisible(state, true), true);
  assert.equal(state.desktopCollapsed, true);
  state = resetNarrowRail(state);
  assert.equal(projectRailVisible(state, false), false);
  let stored = "";
  assert.equal(persistProjectRailCollapsed(value => { stored = value; }, state.desktopCollapsed), true);
  assert.equal(stored, "collapsed");
  assert.equal(persistProjectRailCollapsed(value => { stored = value; }, false), true);
  assert.equal(stored, "expanded");
});

test("malformed or inaccessible local storage falls back to expanded, failed writes do not change rail state", () => {
  for (const value of [null, "true", "COLLAPSED", '{"collapsed":true}', "-1"])
    assert.equal(restoreProjectRailCollapsed(() => value), false);
  assert.equal(restoreProjectRailCollapsed(() => { throw Error("blocked storage"); }), false);
  assert.equal(persistProjectRailCollapsed(() => { throw Error("quota"); }, true), false);
  assert.equal(projectRailVisible({ desktopCollapsed: true, narrowOpen: false }, false), false);
});

test("opening focuses the selected row, or the first row when none is; closing restores the toggle only for rail focus", () => {
  const calls: string[] = [];
  const selected = { focus: () => calls.push("selected") } as unknown as HTMLButtonElement;
  const first = { focus: () => calls.push("first") } as unknown as HTMLButtonElement;
  const rail = { querySelector: () => selected } as unknown as Pick<HTMLElement, "querySelector">;
  assert.equal(focusVisibleProject(rail), true);
  assert.deepEqual(calls, ["selected"]);
  // No row is pressed (the selected project is not listed): the first row of the Explorer takes the focus.
  assert.equal(focusVisibleProject({ querySelector: (selector: string) => selector.includes("aria-pressed") ? null : first } as unknown as Pick<HTMLElement, "querySelector">), true);
  assert.deepEqual(calls, ["selected", "first"]);
  assert.equal(focusVisibleProject({ querySelector: () => null } as unknown as Pick<HTMLElement, "querySelector">), false);
  assert.equal(focusVisibleProject(null), false);
  const active = {} as Node;
  const inside = { contains: (target: Node | null) => target === active };
  const toggle = { focus: () => calls.push("toggle") };
  assert.equal(restoreProjectRailFocus(inside, {} as Node, toggle), false);
  assert.equal(restoreProjectRailFocus(inside, active, toggle), true);
  assert.deepEqual(calls, ["selected", "first", "toggle"]);
});
