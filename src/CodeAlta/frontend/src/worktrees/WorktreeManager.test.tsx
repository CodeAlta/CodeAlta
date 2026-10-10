import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { WorktreeDiscardReview, WorktreeRemovalResults, WorktreeRemovalReview, WorktreeTable } from "./WorktreeManager";
import type { InventoryRow, RemovalOutcome } from "./worktreeInventory";

const never = () => assert.fail("rendering must not act");
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const row = (name: string, change: Partial<InventoryRow> = {}): InventoryRow => ({ path: `C:\\trees\\alpha\\${name}`, name, branch: `alta/${name}`, head: "1234567",
  main: false, project: false, locked: false, missing: false, folder: `C:\\trees\\alpha\\${name}`, busy: false, protection: null, sessions: [], sessionCount: 0, lastUsedAt: null, ...change });
const project = row("alpha", { path: "C:\\code\\alpha", folder: "C:\\code\\alpha", branch: "main", main: true, project: true, protection: "main" });
const used = row("quiet-heron", { lastUsedAt: "2026-10-03T08:00:00+00:00", sessionCount: 3, sessions: [{ id: "s1", title: "Fix the parser", updatedAt: "2026-10-03T08:00:00+00:00", running: false }] });
const orphan = row("amber-denali");
const busy = row("busy-finch", { busy: true, protection: "in_use", sessionCount: 1, lastUsedAt: "2026-10-04T08:00:00+00:00",
  sessions: [{ id: "s2", title: "Port the tests", updatedAt: "2026-10-04T08:00:00+00:00", running: true }] });
const locked = row("kept-ibis", { locked: true, protection: "locked" });
const stale = row("lost-otter", { missing: true });
const outcome = (value: InventoryRow, status: string, change: Partial<RemovalOutcome> = {}): RemovalOutcome =>
  ({ row: value, path: value.path, status, message: null, branchKept: null, branchDeleted: null, ...change });
const table = (rows: readonly InventoryRow[], selected: readonly InventoryRow[] = [], sessionsKnown = true) => render(createElement(WorktreeTable, {
  inventory: { rows, sessionsKnown }, projectName: "Alpha", selected: new Set(selected.map(value => value.path)), busy: false,
  onToggle: never, onToggleAll: never, onRemove: never, onOpenEditor: never, onShowChanges: never }));
const lines = (html: string) => Object.fromEntries(html.split('<tr class="worktree-line"').slice(1).map(line => [/data-path="([^"]+)"/.exec(line)![1].split("\\").pop()!, line.split("</tr>")[0]]));
const button = (line: string, label: string) => new RegExp(`<button[^>]*aria-label="${label}"[^>]*>`).exec(line)?.[0] ?? assert.fail(`${label} is missing`);

test("the window lists what is on disk apart from what git only still lists, and says what protects each checkout", () => {
  const html = table([project, used, orphan, busy, locked, stale], [used]);
  // Two groups: the checkouts whose folder is there, then the ones git lists without their folder.
  const [present, gone] = html.split('<tbody data-group="gone">');
  assert.match(present, /<tbody data-group="present"><tr class="worktree-group"><th colSpan="5" scope="rowgroup">On disk<span class="worktree-group-count">5<\/span>/);
  assert.match(gone, /Listed by git, folder gone<span class="worktree-group-count">1<\/span>/);
  assert.equal((present.match(/class="worktree-line"/g) ?? []).length, 5);
  assert.equal((gone.match(/class="worktree-line"/g) ?? []).length, 1);
  const line = lines(html);

  // The folder of the project: named as the project, never selected, never removed, and still opened and compared.
  assert.match(line.alpha, /<strong>Alpha<\/strong><code title="C:\\code\\alpha">C:\\code\\alpha<\/code>/);
  assert.match(line.alpha, /data-state="main"[^>]*>Project folder</);
  assert.match(line.alpha, /<td class="worktree-cell-select" title="This is the folder of the project: it is not a worktree to remove\."><label[^>]*><input[^>]*disabled=""/);
  assert.match(button(line.alpha, "Remove the worktree alpha"), /disabled=""/);
  assert.doesNotMatch(button(line.alpha, "Open alpha in the code editor"), /disabled/);
  assert.doesNotMatch(button(line.alpha, "Show the changes of alpha"), /disabled/);
  assert.equal(line.alpha.includes("data-removable"), false);

  // A worktree that can go: selected, with its branch, its path, and who used it last.
  assert.match(line["quiet-heron"], /^ data-path="C:\\trees\\alpha\\quiet-heron" data-removable="true" data-selected="true"/);
  assert.match(line["quiet-heron"], /<input[^>]*type="checkbox"[^>]*checked=""/);
  assert.match(line["quiet-heron"], /alta\/quiet-heron/);
  assert.match(line["quiet-heron"], /<small>Fix the parser<\/small><small>3 sessions<\/small>/);
  assert.doesNotMatch(button(line["quiet-heron"], "Remove the worktree quiet-heron"), /disabled/);
  assert.equal(line["quiet-heron"].includes('class="worktree-state"'), false, "nothing protects it");

  // One no session records is listed all the same, can go, and its last use is unknown rather than guessed.
  assert.match(line["amber-denali"], /data-removable="true"/);
  assert.match(line["amber-denali"], /class="worktree-use" data-unknown="true" title="No session records this worktree: when it was last used is not known\.">Unknown</);
  assert.equal(line["amber-denali"].includes("data-selected"), false);

  // A session is at work in it: said with the session, and it is neither selected nor removed.
  assert.match(line["busy-finch"], /data-state="busy" title="A session is working here\nPort the tests">/);
  assert.match(line["busy-finch"], />In use</);
  assert.match(line["busy-finch"], /<td class="worktree-cell-select" title="A session is working here"><label[^>]*><input[^>]*disabled=""/);
  assert.match(button(line["busy-finch"], "Remove the worktree busy-finch"), /disabled=""/);
  assert.match(line["kept-ibis"], /data-state="locked"[^>]*>Locked</);
  assert.match(button(line["kept-ibis"], "Remove the worktree kept-ibis"), /disabled=""/);

  // A folder that is gone has nothing to open or compare; git can forget it.
  assert.match(line["lost-otter"], /data-removable="true" data-missing="true"/);
  assert.match(line["lost-otter"], /data-state="missing"[^>]*>Folder gone</);
  assert.match(button(line["lost-otter"], "Open lost-otter in the code editor"), /disabled=""/);
  assert.match(button(line["lost-otter"], "Show the changes of lost-otter"), /disabled=""/);
  assert.doesNotMatch(button(line["lost-otter"], "Remove the worktree lost-otter"), /disabled/);

  // The box of the header is neither checked nor empty while some are selected.
  assert.match(html, /<thead>.*aria-label="Select every worktree that can be removed"/);
});

test("a repository without a stale worktree shows one group, and a catalog that cannot be read says so", () => {
  const html = table([project, orphan], [], false);
  assert.equal(html.includes('data-group="gone"'), false);
  // Git lists more checkouts than the host holds in one answer: it is said under the list.
  assert.equal(html.includes("worktree-more"), false);
  assert.match(render(createElement(WorktreeTable, { inventory: { rows: [project, orphan], sessionsKnown: true, truncated: true }, projectName: "Alpha", selected: new Set<string>(), busy: false,
    onToggle: never, onToggleAll: never, onRemove: never, onOpenEditor: never, onShowChanges: never })), /<tfoot><tr><td colSpan="5" class="worktree-more">Git lists more worktrees than this window shows\.<\/td>/);
  assert.match(lines(html)["amber-denali"], /title="The sessions could not be read\.">Unknown</);
  // The main checkout of a repository whose project lives in a worktree is protected, and its changes are shown like those of any checkout.
  const main = lines(table([row("repository", { main: true, protection: "main", branch: "main" }), { ...project, path: "C:\\trees\\alpha\\home", name: "home" }]));
  assert.match(main.repository, /data-state="main"[^>]*>Main checkout</);
  assert.doesNotMatch(button(main.repository, "Show the changes of repository"), /disabled/);
  assert.doesNotMatch(button(main.repository, "Open repository in the code editor"), /disabled/);
  assert.match(main.repository, /<td class="worktree-cell-select" title="The checkout the repository lives in is not removed\."><label[^>]*><input[^>]*disabled=""/);
  // The checkout of the project beside it stays the folder of the project, and is not removed either.
  assert.match(main.home, /data-state="main"[^>]*>Project folder</);
  assert.match(button(main.home, "Remove the worktree home"), /disabled=""/);
  assert.match(button(main.repository, "Remove the worktree repository"), /disabled=""/);
});

test("the last-used title is withheld when five older running sessions hide the latest completed session", () => {
  const latest = "2026-10-06T08:00:00+00:00";
  const html = table([row("busy-finch", { busy: true, protection: "in_use", lastUsedAt: latest, sessionCount: 6,
    sessions: Array.from({ length: 5 }, (_, index) => ({ id: `s${index}`, title: `Older running ${index}`, updatedAt: `2026-10-0${index + 1}T08:00:00+00:00`, running: true })) })]);
  assert.match(html, /<time dateTime="2026-10-06T08:00:00\.000Z">/);
  assert.match(html, /<small>6 sessions<\/small>/);
  assert.doesNotMatch(html, /<small>Older running/);
  assert.match(html, /Running · Older running 4/, "The bounded session list remains available in the tooltip.");
});

test("removing asks first with every worktree named, and keeps every branch unless it is asked otherwise", () => {
  const review = (rows: readonly InventoryRow[], deleteBranches = false) => render(createElement(WorktreeRemovalReview, { rows, projectName: "Alpha", deleteBranches,
    onDeleteBranches: never, onCancel: never, onConfirm: never }));
  const many = review([used, orphan, stale]);
  assert.match(many, /<h3>Remove these 3 worktrees\?<\/h3>/);
  assert.match(many, /A worktree that holds changes that are not committed is not removed/);
  // What git ignores is not a change that keeps a worktree: the question says that it goes with the folder.
  assert.match(many, /<p class="worktree-review-ignored">Files that git ignores are deleted with the folder\.<\/p>/);
  // Where no folder is left there is nothing of the kind to lose.
  assert.equal(review([stale]).includes("worktree-review-ignored"), false);
  assert.match(many, /<p>3 sessions recorded them: they continue in the folder of the project\.<\/p>/);
  assert.match(many, /<p>1 has no folder any more: git only forgets it\.<\/p>/);
  assert.deepEqual([...many.matchAll(/<li data-path="([^"]+)"/g)].map(match => match[1]), [used.path, orphan.path, stale.path]);
  assert.match(many, /Last used /);
  assert.match(many, /Last use unknown/);
  // The branches stay: the box is there, and it is not checked.
  assert.match(many, /<input[^>]*type="checkbox"[^>]*\/>/);
  assert.equal(/<input[^>]*checked=""/.test(many), false);
  assert.match(many, /Every branch is kept\./);
  assert.match(many, /<span class="bp6-button-text">Remove 3 worktrees<\/span>/);
  assert.match(review([orphan], true), /A branch that holds commits of its own is kept\./);
  const one = review([orphan]);
  assert.match(one, /<h3>Remove this worktree\?<\/h3>/);
  assert.match(one, /<span class="bp6-button-text">Remove the worktree<\/span>/);
  assert.match(one, /Files that git ignores are deleted with the folder\./);
  assert.equal(one.includes("recorded them"), false);
});

test("throwing changes away is asked by itself, for the worktrees that hold them", () => {
  const html = render(createElement(WorktreeDiscardReview, { rows: [used, orphan], projectName: "Alpha", onCancel: never, onConfirm: never }));
  assert.match(html, /data-discard="true"/);
  assert.match(html, /<h3>Remove these 2 worktrees with their changes\?<\/h3>/);
  assert.match(html, /What is not committed in them is deleted with their folders\. It cannot be recovered\./);
  assert.match(render(createElement(WorktreeDiscardReview, { rows: [used], projectName: "Alpha", onCancel: never, onConfirm: never })), /What is not committed in it is deleted with its folder\./);
  assert.deepEqual([...html.matchAll(/<li data-path="([^"]+)"/g)].map(match => match[1]), [used.path, orphan.path]);
  assert.match(html, /<span class="bp6-button-text">Discard their changes and remove 2<\/span>/);
  assert.match(render(createElement(WorktreeDiscardReview, { rows: [used], projectName: "Alpha", onCancel: never, onConfirm: never })),
    /<h3>Remove this worktree with its changes\?<\/h3>.*Discard its changes and remove it/s);
});

test("a batch that went through in part says what became of each worktree", () => {
  const results = (outcomes: readonly RemovalOutcome[]) => render(createElement(WorktreeRemovalResults, { outcomes, projectName: "Alpha", onDiscard: never, onDone: never }));
  const mixed = results([outcome(used, "ok", { branchDeleted: "alta/quiet-heron" }), outcome(orphan, "dirty"), outcome(busy, "in_use"),
    outcome(locked, "failed", { message: "fatal: cannot remove a locked working tree" }), outcome(stale, "ok")]);
  assert.match(mixed, /<h3 role="status">2 of 5 removed, 3 not removed\.<\/h3>/);
  const items = Object.fromEntries(mixed.split("<li ").slice(1).map(item => [/data-path="[^"]*\\([^"\\]+)"/.exec(item)![1], item.split("</li>")[0]]));
  assert.match(items["quiet-heron"], /data-status="ok".*<strong>Removed<\/strong><span>The branch alta\/quiet-heron was deleted\.<\/span>/s);
  assert.match(items["amber-denali"], /data-status="dirty".*<strong>Not removed<\/strong><span>The worktree holds changes that are not committed\.<\/span>/s);
  assert.match(items["busy-finch"], /data-status="in_use".*<strong>Not removed<\/strong><span>A session is working there\. Wait for it to finish, or stop it\.<\/span>/s);
  assert.match(items["kept-ibis"], /data-status="failed".*<span>fatal: cannot remove a locked working tree<\/span>/s);
  assert.match(items["lost-otter"], /data-status="ok".*<strong>Forgotten by git<\/strong>/s);
  // What holds changes is offered again, by itself.
  assert.match(mixed, /1 worktree holds changes that are not committed\. It was not removed\./);
  assert.match(mixed, /<span class="bp6-button-text">Remove it with its changes…<\/span>/);

  const all = results([outcome(used, "ok"), outcome(orphan, "ok", { branchKept: "alta/amber-denali" })]);
  assert.match(all, /<h3 role="status">2 worktrees were removed\.<\/h3>/);
  assert.match(all, /The branch alta\/amber-denali is kept: it holds commits of its own\./);
  assert.equal(all.includes("worktree-results-dirty"), false);
  assert.match(results([outcome(used, "ok")]), /<h3 role="status">The worktree was removed\.<\/h3>/);
  // An answer that did not arrive concludes nothing, and what was not started says so.
  const lost = results([outcome(used, "unconfirmed"), outcome(orphan, "canceled")]);
  assert.match(lost, /<h3 role="status">0 of 2 removed, 1 not removed, 1 unknown\.<\/h3>/);
  assert.match(lost, /data-status="unconfirmed".*?<strong>Outcome unknown<\/strong>/s);
  assert.match(lost, /The answer did not arrive: the list says whether it is still there\./);
  assert.match(lost, /Not started\./);
});

test("a malformed removal answer says outcome unknown without counting it as definitely not removed", () => {
  const html = render(createElement(WorktreeRemovalResults, { outcomes: [outcome(used, "read_failed"), outcome(orphan, "dirty"), outcome(stale, "ok")],
    projectName: "Alpha", onDiscard: never, onDone: never }));
  assert.match(html, /<h3 role="status">1 of 3 removed, 1 not removed, 1 unknown\.<\/h3>/);
  assert.match(html, /data-status="read_failed".*?<strong>Outcome unknown<\/strong>/s);
  assert.equal((html.match(/<strong>Not removed<\/strong>/g) ?? []).length, 1);
});
