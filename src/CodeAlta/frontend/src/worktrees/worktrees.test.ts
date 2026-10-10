import assert from "node:assert/strict";
import test from "node:test";
import { translate } from "../localization";
import { branchesReply, checkoutFolder, checkoutShown, filterBranches, folderName, newBranchName, persistWorkPlaces, projectFolder, referenceName, restoreWorkPlaces, sameFolder,
  sessionWorktree, shownCheckout, withWorkPlace, worktreeFailure, worktreeSessions, worktreesReply, type Branch, type Worktree } from "./worktrees";

const t = (key: Parameters<typeof translate>[1]) => translate("en", key);
const worktree = (change: Partial<Worktree> = {}): Worktree => ({ path: "C:\\trees\\app\\quiet-heron", name: "quiet-heron", branch: "alta/quiet-heron", head: "1234567",
  main: false, project: false, locked: false, missing: false, folder: "C:\\trees\\app\\quiet-heron", busy: false, ...change });
const branch = (name: string, change: Partial<Branch> = {}): Branch => ({ name, current: false, remote: false, worktree: null, worktreeName: null, ...change });
const session = (worktreePath: string | null, worktreeRoot: string | null = worktreePath, worktreeMissing = false) =>
  ({ worktreePath, worktreeRoot, worktreeName: worktreePath && folderName(worktreePath), worktreeMissing });

test("the place of a project is its folder unless a new worktree was chosen, and only that is stored", () => {
  let stored: string | null = null;
  const none = restoreWorkPlaces(() => stored);
  assert.equal(none.size, 0);
  assert.deepEqual(projectFolder, { worktree: false, base: null });

  const chosen = withWorkPlace(withWorkPlace(none, "a", { worktree: true, base: "main" }), "b", { worktree: true, base: null });
  assert.equal(persistWorkPlaces(value => { stored = value; }, chosen), true);
  assert.deepEqual([...restoreWorkPlaces(() => stored)], [["a", { worktree: true, base: "main" }], ["b", { worktree: true, base: null }]]);

  // Back in the folder of the project: no entry is kept.
  const back = withWorkPlace(chosen, "a", { worktree: false, base: "main" });
  assert.deepEqual([...back.keys()], ["b"]);

  // What cannot be a choice reads as none, and a base that git would not take is dropped.
  for (const text of ["", "[]", "nope", JSON.stringify({ a: { worktree: false } }), JSON.stringify({ a: null }), "x".repeat(70_000)])
    assert.equal(restoreWorkPlaces(() => text).size, 0, text.slice(0, 30));
  assert.deepEqual([...restoreWorkPlaces(() => JSON.stringify({ a: { worktree: true, base: "--force" } }))], [["a", { worktree: true, base: null }]]);
  assert.equal(restoreWorkPlaces(() => { throw new Error("no storage"); }).size, 0);
  assert.equal(persistWorkPlaces(() => { throw new Error("full"); }, chosen), false);
});

test("a name handed to git is never an option nor a name git forbids", () => {
  for (const name of ["main", "alta/quiet-heron", "origin/release/2.0", "v1.2.3", "feature_x"]) assert.equal(referenceName(name), true, name);
  for (const name of ["", "-f", "--force", "a b", "a..b", "a~1", "a^", "a:b", "a?", "a*", "[a", "a\\b", "a@{1}", "/a", "a/", "a//b", "a/.b", "a.lock", "a.", ".a", "@", "a\tb", "x".repeat(201)])
    assert.equal(referenceName(name), false, name);
});

test("a session row names its worktree, and a checkout finds the sessions that work in it", () => {
  assert.equal(sessionWorktree(session(null)), null);
  assert.equal(sessionWorktree(undefined), null);
  assert.deepEqual(sessionWorktree(session("C:\\trees\\app\\quiet-heron\\src", "C:\\trees\\app\\quiet-heron")),
    { path: "C:\\trees\\app\\quiet-heron\\src", root: "C:\\trees\\app\\quiet-heron", name: "src", missing: false });
  assert.equal(sessionWorktree({ worktreePath: "/trees/gone", worktreeRoot: null, worktreeName: null, worktreeMissing: true })?.name, "gone");
  assert.equal(sessionWorktree({ worktreePath: "/trees/gone", worktreeRoot: null, worktreeName: null, worktreeMissing: true })?.missing, true);

  const sessions = [session(null), session("c:/trees/app/quiet-heron"), session("C:\\trees\\app\\quiet-heron-2"), session("C:\\trees\\app\\quiet-heron", "C:\\trees\\app\\quiet-heron", true)];
  // Windows folders differ neither by case nor by the kind of slash; another folder that starts the same is another one.
  assert.deepEqual(worktreeSessions(sessions, worktree()), [sessions[1]]);
  // A session whose worktree is gone works in the folder of its project again.
  assert.deepEqual(worktreeSessions(sessions, worktree({ main: true, project: true, path: "C:\\code\\app", folder: "C:\\code\\app" })), [sessions[0], sessions[3]]);
  // The main checkout of a repository whose project lives in a worktree is not where those sessions work: only a session that records it does.
  assert.deepEqual(worktreeSessions(sessions, worktree({ main: true, path: "C:\\code\\repository", folder: "C:\\code\\repository" })), []);
  assert.deepEqual(worktreeSessions([...sessions, session("C:\\code\\repository")], worktree({ main: true, path: "C:\\code\\repository", folder: "C:\\code\\repository" })).length, 1);
  assert.equal(sameFolder("/trees/App", "/trees/app"), false);
  assert.equal(sameFolder("/trees/app/", "/trees/app"), true);
  assert.equal(sameFolder(null, "/trees/app"), false);
});

test("a project that lives in a worktree shows the main checkout of its repository, its own checkout and another worktree as three checkouts", () => {
  const repository = worktree({ path: "C:\\code\\repository", folder: "C:\\code\\repository\\src", name: "repository", branch: "main", main: true });
  const home = worktree({ path: "C:\\trees\\app\\home", folder: "C:\\trees\\app\\home\\src", name: "home", main: true, project: true });
  const other = worktree({ path: "C:\\trees\\app\\other", folder: "C:\\trees\\app\\other\\src", name: "other" });
  const list = [repository, home, other];

  // What the Changes tab is asked for: nothing for the checkout the project lives in, its own folder for each other one.
  assert.deepEqual(list.map(checkoutFolder), ["C:\\code\\repository\\src", null, "C:\\trees\\app\\other\\src"]);
  // One row is the one shown, whichever checkout is: the two that are not removed are not taken for each other.
  const chosen = (shown: string | null) => list.filter(value => checkoutShown(value, shown)).map(value => value.name);
  assert.deepEqual(chosen(null), ["home"]);
  assert.deepEqual(chosen("c:/code/repository/src/"), ["repository"]);
  assert.deepEqual(chosen("C:\\trees\\app\\other\\src"), ["other"]);
  assert.deepEqual(chosen("C:\\trees\\app\\gone"), []);

  // The main checkout that was asked for is still the one shown when the list is read again.
  assert.equal(shownCheckout(list, "C:\\code\\repository\\src"), repository);
  assert.equal(shownCheckout(list, "C:\\trees\\app\\other\\src"), other);
  assert.equal(shownCheckout(list, null), null);
  // A checkout that is gone, or whose folder is, gives way to the folder of the project.
  assert.equal(shownCheckout([repository, home], "C:\\trees\\app\\other\\src"), null);
  assert.equal(shownCheckout([repository, home, { ...other, missing: true }], "C:\\trees\\app\\other\\src"), null);
  // The folder of the project is never shown as another checkout.
  assert.equal(shownCheckout(list, "C:\\trees\\app\\home\\src"), null);

  // A project in the main checkout of its repository, as most are: one row is both, and nothing changes.
  const plain = worktree({ path: "C:\\code\\app", folder: "C:\\code\\app", name: "app", branch: "main", main: true, project: true });
  assert.deepEqual([plain, other].map(checkoutFolder), [null, "C:\\trees\\app\\other\\src"]);
  assert.deepEqual([plain, other].filter(value => checkoutShown(value, null)).map(value => value.name), ["app"]);
  assert.equal(shownCheckout([plain, other], "C:\\code\\app"), null);
});

test("the list of checkouts is taken only when it is whole and for the asked project", () => {
  const item = { ...worktree() };
  assert.deepEqual(worktreesReply({ status: "ok", projectId: "p", worktrees: [item], newFolder: "C:\\trees\\app" }, "p"), { worktrees: [item], newFolder: "C:\\trees\\app" });
  assert.equal(worktreesReply({ status: "not_repository", projectId: "p", worktrees: [], newFolder: null }, "p"), "not_repository");
  assert.equal(worktreesReply({ status: "ok", projectId: "other", worktrees: [item], newFolder: null }, "p"), "read_failed");
  assert.equal(worktreesReply({ status: "ok", projectId: "p", worktrees: [{ ...item, busy: "no" }], newFolder: null }, "p"), "read_failed");
  assert.equal(worktreesReply({ status: "ok", projectId: "p", worktrees: [{ ...item, path: "" }], newFolder: null }, "p"), "read_failed");
  // Which checkout the project lives in is part of the answer: one at most, and one that is not removed.
  const home = { ...item, path: "C:\\trees\\app\\home", folder: "C:\\trees\\app\\home", main: true, project: true };
  const repository = { ...item, path: "C:\\code\\repository", folder: "C:\\code\\repository", main: true };
  assert.deepEqual(worktreesReply({ status: "ok", projectId: "p", worktrees: [repository, home, item], newFolder: null }, "p"), { worktrees: [repository, home, item], newFolder: null });
  assert.equal(worktreesReply({ status: "ok", projectId: "p", worktrees: [{ ...item, project: undefined }], newFolder: null }, "p"), "read_failed");
  assert.equal(worktreesReply({ status: "ok", projectId: "p", worktrees: [{ ...item, project: true }], newFolder: null }, "p"), "read_failed");
  assert.equal(worktreesReply({ status: "ok", projectId: "p", worktrees: [home, { ...repository, project: true }], newFolder: null }, "p"), "read_failed");
  assert.equal(worktreesReply(null, "p"), "read_failed");
});

test("branches are filtered by what is typed, and a new name is offered only when no branch has it", () => {
  const list = [branch("feature/login"), branch("main", { current: true }), branch("origin/release", { remote: true }), branch("alta/quiet-heron", { worktree: "C:\\trees", worktreeName: "quiet-heron" })];
  assert.deepEqual(branchesReply({ status: "ok", branches: list, busy: true }), { branches: list, busy: true });
  assert.equal(branchesReply({ status: "in_use", branches: [], busy: false }), "in_use");
  assert.equal(branchesReply({ status: "ok", branches: [{ ...list[0], remote: 1 }], busy: false }), "read_failed");

  assert.deepEqual(filterBranches(list, "").map(value => value.name), ["main", "feature/login", "origin/release", "alta/quiet-heron"]);
  assert.deepEqual(filterBranches(list, " LOG  feat ").map(value => value.name), ["feature/login"]);
  assert.equal(newBranchName(" topic/new ", list), "topic/new");
  assert.equal(newBranchName("main", list), null);
  // A branch of a remote becomes a local branch of that name: the name is taken.
  assert.equal(newBranchName("release", list), null);
  assert.equal(newBranchName("not a name", list), null);
  assert.equal(newBranchName("", list), null);
});

test("a refusal is told in the words of the window, and what git said is kept for what has none", () => {
  assert.equal(worktreeFailure("in_use", "ignored", t), "A session is working there. Wait for it to finish, or stop it.");
  assert.equal(worktreeFailure("locked", null, t), "Git keeps this worktree locked: unlock it with git to remove it.");
  assert.equal(worktreeFailure("failed", " error: Your local changes would be overwritten ", t), "error: Your local changes would be overwritten");
  assert.equal(worktreeFailure("failed", null, t), "Git could not do it.");
  assert.equal(worktreeFailure("something_new", "", t), "Git could not do it.");
});
