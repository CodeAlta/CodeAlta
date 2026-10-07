import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ProjectContext } from "../ProjectContext";
import { ShellLanguageContext } from "../shellLanguage";
import { WorktreeList } from "./WorktreeList";
import { WorktreeSettings } from "./WorktreeSettings";
import type { Worktree } from "./worktrees";

const never = () => assert.fail("rendering must not act");
const project = { id: "p", name: "Alpha", path: "C:\\code\\alpha" };
const worktree = (change: Partial<Worktree> = {}): Worktree => ({ path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", branch: "alta/quiet-heron", head: "1234567",
  main: false, locked: false, missing: false, folder: "C:\\trees\\alpha\\quiet-heron", busy: false, ...change });
const main = worktree({ path: project.path, folder: project.path, name: "alpha", branch: "main", main: true });
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
// The host is asked nothing while a view is drawn.
const silent = () => new Promise<never>(() => { });

test("the composer names the project, and a session in a worktree shows it as what it is", () => {
  const plain = render(createElement(ProjectContext, { epoch: "e", project, read: silent }));
  assert.match(plain, /class="project-context-folder"[^>]*>.*<strong>Alpha<\/strong><span>C:\\code\\alpha<\/span>/);
  assert.equal(plain.includes("project-context-worktree"), false);

  const inWorktree = render(createElement(ProjectContext, { epoch: "e", project, read: silent, worktree: { path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", missing: false } }));
  assert.match(inWorktree, /class="project-context-worktree" title="Works in a git worktree of Alpha\nC:\\trees\\alpha\\quiet-heron"/);
  assert.match(inWorktree, /<strong>quiet-heron<\/strong><span>C:\\trees\\alpha\\quiet-heron<\/span>/);
  // The folder of the project is not shown as where the session works.
  assert.equal(inWorktree.includes("<span>C:\\code\\alpha</span>"), false);
  // Where the changes can be shown, the worktree leads to them.
  const linked = render(createElement(ProjectContext, { epoch: "e", project, read: silent, onShowChanges: never,
    worktree: { path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", missing: false } }));
  assert.match(linked, /<button type="button" class="project-context-worktree" title="Works in a git worktree of Alpha\nC:\\trees\\alpha\\quiet-heron\nShow changes">/);

  const gone = render(createElement(ProjectContext, { epoch: "e", project, read: silent, worktree: { path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", missing: true } }));
  assert.match(gone, /class="project-context-worktree" data-missing="true" title="The folder of this worktree is gone: the session continues in the folder of the project\./);
  assert.match(gone, /<strong>quiet-heron<\/strong><span>removed<\/span>/);
});

test("a new session says where it will work once a worktree is chosen, also before the repository answered", () => {
  // Nothing is offered before the folder is known to be in a repository.
  const unknown = render(createElement(ProjectContext, { epoch: "e", project, read: silent, place: { value: { worktree: false, base: null }, onChange: never } }));
  assert.equal(unknown.includes("project-context-place"), false);
  // A worktree that was chosen is never hidden: it can always be changed back.
  const chosen = render(createElement(ProjectContext, { epoch: "e", project, read: silent, place: { value: { worktree: true, base: null }, onChange: never } }));
  assert.match(chosen, /<button type="button" class="project-context-place" data-worktree="true"[^>]*title="The next session works in a new git worktree"/);
  assert.match(chosen, /<span>New worktree<\/span>/);
  const based = render(createElement(ProjectContext, { epoch: "e", project, read: silent, place: { value: { worktree: true, base: "release/2" }, onChange: never } }));
  assert.match(based, /<span>New worktree from release\/2<\/span>/);
  // A window without a host offers nothing.
  assert.equal(render(createElement(ProjectContext, { epoch: null, project, read: silent, place: { value: { worktree: true, base: null }, onChange: never } })).includes("project-context-place"), false);
});

test("the checkouts of a repository list the folder of the project first, then its worktrees with what works in them", () => {
  const html = render(createElement(WorktreeList, { epoch: "e", projectId: "p", projectName: "Alpha", selected: null, onSelect: never, onRemoved: never,
    worktrees: [main, worktree({ busy: true }), worktree({ path: "C:\\trees\\alpha\\amber-denali", folder: "C:\\trees\\alpha\\amber-denali", name: "amber-denali", branch: null, missing: true })],
    sessions: value => value.name === "quiet-heron" ? 2 : 0 }));
  const rows = html.split('<div class="worktree-row"').slice(1);
  assert.equal(rows.length, 3);
  // The folder of the project is the one shown, under the name of the project, and it is not one to remove.
  assert.match(rows[0], /^ data-main="true"><button type="button" role="option" aria-selected="true"/);
  assert.match(rows[0], /<strong>Alpha<\/strong>/);
  assert.equal(rows[0].includes("worktree-row-remove"), false);
  // A worktree a session is working in is not removed.
  assert.match(rows[1], /<strong>quiet-heron<\/strong>/);
  assert.match(rows[1], /alta\/quiet-heron/);
  assert.match(rows[1], /<span>2 sessions<\/span>/);
  assert.match(rows[1], /class="worktree-row-busy" title="A session is working here"/);
  assert.match(rows[1], /<button[^>]*class="[^"]*worktree-row-remove[^"]*"[^>]*disabled=""/);
  // One whose folder is gone can only be removed from the list.
  assert.match(rows[2], /^ data-missing="true"><button type="button" role="option" aria-selected="false" disabled=""/);
  assert.match(rows[2], /Folder gone/);
  assert.equal(/<button[^>]*worktree-row-remove[^>]*disabled=""/.test(rows[2]), false);

  const selected = render(createElement(WorktreeList, { epoch: "e", projectId: "p", projectName: "Alpha", selected: "c:/trees/alpha/quiet-heron/", onSelect: never, onRemoved: never,
    worktrees: [main, worktree()], sessions: () => 0 }));
  assert.match(selected.split('<div class="worktree-row"')[2], /aria-selected="true"/);
  assert.match(selected.split('<div class="worktree-row"')[1], /aria-selected="false"/);
});

test("the settings of the worktrees are read before they are shown", () => {
  const html = render(createElement(WorktreeSettings, { epoch: "e", api: { settings: silent, saveSettings: never } }));
  assert.match(html, /<h1>Worktrees<\/h1>/);
  assert.match(html, /Where the git worktrees of sessions are created\./);
  assert.match(html, /Loading…/);
});
