import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { WorkspaceChip } from "./WorkspaceChip";

const never = () => assert.fail("rendering must not act");
const project = { id: "p", name: "Alpha", path: "C:\\code\\alpha" };
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
// The host is asked nothing while the chip is drawn: the repository is not known yet.
const silent = () => new Promise<never>(() => { });

test("a session in a worktree says so on the Workspace chip, in words and in the tone of worktrees", () => {
  const html = render(createElement(WorkspaceChip, { epoch: "e", project, read: silent, worktree: { path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", missing: false } }));
  assert.match(html, /class="workspace-chip-part workspace-chip-place" data-worktree="yes"[^>]*aria-label="Works in the worktree quiet-heron of Alpha"/);
  assert.match(html, /title="Works in the worktree quiet-heron of Alpha\nC:\\trees\\alpha\\quiet-heron"/);
  assert.match(html, /<span class="workspace-chip-kind">Worktree<\/span><span class="workspace-chip-name">quiet-heron<\/span>/);
  // A session in the folder of the project has no worktree to show, and offers no choice: it exists already.
  const plain = render(createElement(WorkspaceChip, { epoch: "e", project, read: silent }));
  assert.equal(plain.includes("data-worktree"), false);
  assert.equal(plain.includes("workspace-chip-work-place"), false);

  const gone = render(createElement(WorkspaceChip, { epoch: "e", project, read: silent, worktree: { path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", missing: true } }));
  assert.match(gone, /data-worktree="missing"[^>]*aria-label="The folder of this worktree is gone: the session continues in the folder of the project\."/);
});

test("the chip of a new session shows where it will work, once a worktree can be made", () => {
  // Nothing is offered before the folder is known to be in a repository.
  const unknown = render(createElement(WorkspaceChip, { epoch: "e", project, read: silent, place: { value: { worktree: false, base: null }, onChange: never } }));
  assert.equal(unknown.includes("workspace-chip-work-place"), false);
  // A worktree that was chosen is always shown, in words, so that it can be changed back.
  const chosen = render(createElement(WorkspaceChip, { epoch: "e", project, read: silent, place: { value: { worktree: true, base: "release/2" }, onChange: never } }));
  assert.match(chosen, /class="workspace-chip-part workspace-chip-work-place" data-worktree="true"[^>]*aria-label="Where to work: New worktree from release\/2" title="The next session works in a new git worktree"/);
  assert.match(chosen, /<span class="workspace-chip-name">New worktree from release\/2<\/span>/);
  // It stands between the project and what follows it.
  assert.ok(chosen.indexOf("workspace-chip-project") < chosen.indexOf("workspace-chip-work-place"));
  // A window without a host offers nothing.
  assert.equal(render(createElement(WorkspaceChip, { epoch: null, project, read: silent, place: { value: { worktree: true, base: null }, onChange: never } }))
    .includes("workspace-chip-work-place"), false);
});
