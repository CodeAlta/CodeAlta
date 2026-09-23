import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { ProjectRailToggle } from "./ProjectRailToggle";
import { collapsedSessionWidth, constrainPaneLayout } from "./paneLayout";
import { projectRailProjection } from "./projectRail";
import { focusVisibleProject, persistProjectRailCollapsed, projectRailVisible, resetNarrowRail, restoreProjectRailCollapsed, restoreProjectRailFocus, toggleProjectRail } from "./projectRailVisibility";
import { resolveShortcut } from "./shortcuts";
import type { WorkspaceSnapshot } from "#neoastra";

test("desktop collapse/reopen and narrow reveal do not change selection, filter, sort or preferred widths", () => {
  let state = { desktopCollapsed: false, narrowOpen: false };
  const selected = { projectId: "p", sessionId: "s", filter: "REPO", sort: "recent" as const };
  const preferred = { projects: 400, sessions: 420 };
  const snapshot: WorkspaceSnapshot = {
    configured: true, projects: [{ id: "p", name: "P", path: "/repo/p", archived: false }], sessions: [],
    projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  };
  const before = projectRailProjection(snapshot, selected.filter, selected.sort);
  assert.equal(projectRailVisible(state, false), true);
  state = toggleProjectRail(state, false);
  assert.equal(projectRailVisible(state, false), false);
  assert.equal(collapsedSessionWidth(preferred, 900), 412);
  state = toggleProjectRail(state, false);
  assert.equal(projectRailVisible(state, false), true);
  // A viewport change hides the project rail until intentionally opened, without persisting the narrow state.
  state = resetNarrowRail(state);
  assert.equal(projectRailVisible(state, true), false);
  state = toggleProjectRail(state, true);
  assert.equal(projectRailVisible(state, true), true);
  assert.equal(state.desktopCollapsed, false);
  assert.deepEqual(projectRailProjection(snapshot, selected.filter, selected.sort), before);
  assert.deepEqual(selected, { projectId: "p", sessionId: "s", filter: "REPO", sort: "recent" });
  assert.deepEqual(constrainPaneLayout(preferred, 1400), preferred);
  assert.equal(projectRailVisible(resetNarrowRail(state), true), false);
  assert.equal(projectRailVisible(resetNarrowRail(state), false), true);
});

test("narrow opening never overrides a persisted desktop collapse and does not serialize constrained widths", () => {
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

test("labelled native button toggles through its pointer/keyboard activation callback; focus chord and Escape respect IME", () => {
  let state = { desktopCollapsed: true, narrowOpen: false };
  const toggle = () => { state = toggleProjectRail(state, false); };
  const closed = ProjectRailToggle({ expanded: projectRailVisible(state, false), onToggle: toggle, buttonRef: null });
  assert.match(renderToStaticMarkup(closed), /<button[^>]*aria-label="Show projects"[^>]*aria-controls="project-rail"[^>]*aria-expanded="false"/);
  assert.match(renderToStaticMarkup(closed), /type="button"/);
  closed.props.onClick(); // Native button activation (pointer, Enter or Space) uses this same callback.
  assert.equal(projectRailVisible(state, false), true);
  const open = ProjectRailToggle({ expanded: projectRailVisible(state, false), onToggle: toggle, buttonRef: null });
  assert.match(renderToStaticMarkup(open), /aria-label="Hide projects"[^>]*aria-controls="project-rail"[^>]*aria-expanded="true"/);
  open.props.onClick();
  assert.equal(projectRailVisible(state, false), false);
  assert.equal(resolveShortcut({ key: "g", ctrlKey: true }, false, false).chordPending, true);
  assert.equal(resolveShortcut({ key: "s" }, true, false).action, "focusProjects");
  assert.equal(resolveShortcut({ key: "Escape" }, false, true).action, "escape");
  assert.equal(resolveShortcut({ key: "Escape", isComposing: true }, false, true).handled, false);
  assert.equal(resolveShortcut({ key: "Escape", keyCode: 229 }, false, true).handled, false);
});

test("opening focuses a visible selected row or the filter when hidden; closing restores the toggle only for rail focus", () => {
  const calls: string[] = [];
  const selected = { focus: () => calls.push("selected") } as unknown as HTMLButtonElement;
  const filter = { focus: () => calls.push("filter") };
  const rail = { querySelector: () => selected } as unknown as Pick<HTMLElement, "querySelector">;
  assert.equal(focusVisibleProject(rail, filter), true);
  assert.deepEqual(calls, ["selected"]);
  assert.equal(focusVisibleProject({ querySelector: () => null } as unknown as Pick<HTMLElement, "querySelector">, filter), true);
  assert.deepEqual(calls, ["selected", "filter"]);
  assert.equal(focusVisibleProject(null, null), false);
  const active = {} as Node;
  const inside = { contains: (target: Node | null) => target === active };
  const toggle = { focus: () => calls.push("toggle") };
  assert.equal(restoreProjectRailFocus(inside, {} as Node, toggle), false);
  assert.equal(restoreProjectRailFocus(inside, active, toggle), true);
  assert.deepEqual(calls, ["selected", "filter", "toggle"]);
});
