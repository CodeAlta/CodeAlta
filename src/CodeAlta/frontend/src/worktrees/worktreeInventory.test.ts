import assert from "node:assert/strict";
import test from "node:test";
import { translate, type MessageKey } from "../localization";
import { editorFailure, inventoryFailure, inventoryGroups, inventoryReply, keepSelection, lastUse, mergeOutcomes, removable, removalChunks, removalCounts, removalReason,
  removalReply, selectedRows, showsChanges, toggleAll, toggleOne, type InventoryRow, type RemovalOutcome } from "./worktreeInventory";

const t = (key: MessageKey) => translate("en", key);
const row = (change: Partial<InventoryRow> = {}): InventoryRow => ({ path: "C:\\trees\\alpha\\quiet-heron", name: "quiet-heron", branch: "alta/quiet-heron", head: "1234567",
  main: false, project: false, locked: false, missing: false, folder: "C:\\trees\\alpha\\quiet-heron", busy: false, protection: null, sessions: [], sessionCount: 0, lastUsedAt: null, ...change });
const named = (name: string, change: Partial<InventoryRow> = {}) => row({ path: `C:\\trees\\alpha\\${name}`, folder: `C:\\trees\\alpha\\${name}`, name, branch: `alta/${name}`, ...change });
const project = row({ path: "C:\\code\\alpha", folder: "C:\\code\\alpha", name: "alpha", branch: "main", main: true, project: true, protection: "main" });
const session = (id: string, updatedAt: string, running = false) => ({ id, title: `Session ${id}`, updatedAt, running });
const outcome = (value: InventoryRow, status: string, change: Partial<RemovalOutcome> = {}): RemovalOutcome =>
  ({ row: value, path: value.path, status, message: null, branchKept: null, branchDeleted: null, ...change });

test("the inventory is taken only when it is whole and for the asked project", () => {
  const used = row({ sessions: [session("s1", "2026-10-01T10:00:00+00:00")], sessionCount: 3, lastUsedAt: "2026-10-01T10:00:00+00:00" });
  const reply = { status: "ok", projectId: "p", worktrees: [project, used], sessionsKnown: true };
  assert.deepEqual(inventoryReply(reply, "p"), { rows: [project, used], sessionsKnown: true });
  // A worktree no session records is listed like the others: its last use is not known.
  assert.deepEqual(inventoryReply({ ...reply, worktrees: [row()], sessionsKnown: false }, "p"), { rows: [row()], sessionsKnown: false });

  assert.equal(inventoryReply({ ...reply, status: "not_repository", worktrees: [] }, "p"), "not_repository");
  // The answer for another project, or one that names a folder twice, says nothing of this one.
  assert.equal(inventoryReply(reply, "other"), "read_failed");
  assert.equal(inventoryReply({ ...reply, worktrees: [used, used] }, "p"), "read_failed");
  for (const broken of [{ ...used, protection: "never" }, { ...used, busy: "no" }, { ...used, path: "" }, { ...used, sessionCount: 0 }, { ...used, lastUsedAt: "yesterday" },
    { ...used, sessions: [{ ...used.sessions[0], running: 1 }] }, { ...used, project: undefined }, null])
    assert.equal(inventoryReply({ ...reply, worktrees: [broken] }, "p"), "read_failed", JSON.stringify(broken));
  assert.equal(inventoryReply({ ...reply, sessionsKnown: undefined }, "p"), "read_failed");
  // The host holds a bounded number of checkouts, and says when git lists more; more than that is no answer of this host.
  assert.deepEqual(inventoryReply({ ...reply, truncated: true }, "p"), { rows: [project, used], sessionsKnown: true, truncated: true });
  assert.deepEqual(inventoryReply({ ...reply, truncated: false }, "p"), { rows: [project, used], sessionsKnown: true });
  assert.equal(inventoryReply({ ...reply, truncated: "yes" }, "p"), "read_failed");
  assert.equal(inventoryReply({ ...reply, worktrees: Array.from({ length: 513 }, (_, index) => named(`tree-${index}`)) }, "p"), "read_failed");
  assert.equal(inventoryReply(null, "p"), "read_failed");
});

test("only a worktree that nothing protects can be asked to go", () => {
  assert.equal(removable(row()), true);
  assert.equal(removable(row({ missing: true })), true, "a worktree whose folder is gone is forgotten");
  assert.equal(removable(project), false);
  assert.equal(removable(row({ main: true, protection: "main" })), false, "the main checkout of the repository");
  assert.equal(removable(row({ busy: true, protection: "in_use" })), false);
  assert.equal(removable(row({ locked: true, protection: "locked" })), false);
  // What the row says protects it, also when the host named no reason.
  for (const flag of ["main", "project", "busy", "locked"] as const) assert.equal(removable(row({ [flag]: true })), false, flag);
  // A session that recorded a worktree in the past does not protect it: only one that is at work now does.
  assert.equal(removable(row({ sessions: [session("s1", "2026-10-01T10:00:00+00:00")], sessionCount: 1, lastUsedAt: "2026-10-01T10:00:00+00:00" })), true);

  assert.equal(showsChanges(project), true);
  assert.equal(showsChanges(row()), true);
  assert.equal(showsChanges(row({ missing: true })), false);
  // The main checkout of a repository whose project lives in a worktree is not what the changes tab shows.
  assert.equal(showsChanges(row({ main: true, protection: "main" })), false);
});

test("the selection holds what can go, and loses what the list no longer offers", () => {
  const heron = named("quiet-heron"), denali = named("amber-denali"), gone = named("lost-otter", { missing: true }), busy = named("busy-finch", { busy: true, protection: "in_use" });
  const rows = [project, heron, denali, busy, gone];
  const all = toggleAll(new Set(), rows);
  assert.deepEqual([...all], [heron.path, denali.path, gone.path]);
  assert.deepEqual([...toggleAll(all, rows)], [], "all selected: the second press clears");
  assert.deepEqual([...toggleAll(new Set([heron.path]), rows)], [heron.path, denali.path, gone.path], "some selected: the press selects them all");
  assert.deepEqual([...toggleAll(new Set(), [project, busy])], []);

  assert.deepEqual([...toggleOne(new Set(), heron)], [heron.path]);
  assert.deepEqual([...toggleOne(new Set([heron.path]), heron)], []);
  assert.deepEqual([...toggleOne(new Set(), busy)], [], "a protected checkout is never selected");
  assert.deepEqual([...toggleOne(new Set(), project)], []);

  // The list was read again: a worktree a session started to work in, and one that is gone, leave the selection.
  const again = [project, { ...heron, busy: true, protection: "in_use" as const }, gone];
  assert.deepEqual([...keepSelection(all, again)], [gone.path]);
  const same = new Set([heron.path]);
  assert.equal(keepSelection(same, rows), same, "the same selection again draws nothing");
  assert.deepEqual(selectedRows(new Set([gone.path, heron.path, busy.path, project.path]), rows).map(value => value.name), ["quiet-heron", "lost-otter"]);

  assert.deepEqual(inventoryGroups(rows), { present: [project, heron, denali, busy], gone: [gone] });
});

test("the last use of a checkout is what its sessions record, and unknown when none does", () => {
  assert.equal(lastUse(row()), null);
  const used = row({ lastUsedAt: "2026-10-03T08:00:00+00:00", sessionCount: 2,
    sessions: [session("running", "2026-10-02T08:00:00+00:00", true), session("latest", "2026-10-03T08:00:00+00:00")] });
  assert.deepEqual(lastUse(used), { at: "2026-10-03T08:00:00+00:00", session: used.sessions[1] });
  // More sessions record it than are named: the time is the host's, whoever is named.
  assert.deepEqual(lastUse(row({ lastUsedAt: "2026-10-03T08:00:00+00:00", sessionCount: 9 })), { at: "2026-10-03T08:00:00+00:00", session: null });
});

test("an answer to a removal is read only as the answer for the folders that were asked", () => {
  const asked = ["C:\\trees\\a", "C:\\trees\\b"];
  const results = [{ path: asked[0], status: "ok", message: null, branchKept: null, branchDeleted: "alta/a" }, { path: asked[1], status: "dirty", message: null, branchKept: null, branchDeleted: null }];
  assert.deepEqual(removalReply({ status: "ok", results }, asked), results);
  // Fields the host left out read as none.
  assert.deepEqual(removalReply({ status: "ok", results: [{ path: asked[0], status: "locked" }] }, [asked[0]]),
    [{ path: asked[0], status: "locked", message: null, branchKept: null, branchDeleted: null }]);
  assert.equal(removalReply({ status: "stale_epoch", results: [] }, asked), "stale_epoch");
  // Fewer results, results in another order, or for other folders: nothing is concluded.
  assert.equal(removalReply({ status: "ok", results: [results[0]] }, asked), "read_failed");
  assert.equal(removalReply({ status: "ok", results: [results[1], results[0]] }, asked), "read_failed");
  assert.equal(removalReply({ status: "ok", results: [results[0], { ...results[1], path: "C:\\trees\\c" }] }, asked), "read_failed");
  assert.equal(removalReply({ status: "ok", results: [results[0], { ...results[1], status: 1 }] }, asked), "read_failed");
  assert.equal(removalReply(undefined, asked), "read_failed");
});

test("a batch says what went, what holds changes and what stays for another reason", () => {
  const a = named("a"), b = named("b"), c = named("c"), d = named("d");
  const first = [outcome(a, "ok", { branchDeleted: "alta/a" }), outcome(b, "dirty"), outcome(c, "in_use"), outcome(d, "failed", { message: "fatal: unable to remove" })];
  assert.deepEqual(removalCounts(first), { removed: 1, dirty: 1, kept: 2 });
  // The worktree that held changes was asked about by itself: what the second request said replaces what was known of it.
  const merged = mergeOutcomes(first, [outcome(b, "ok")]);
  assert.deepEqual(merged.map(value => [value.row.name, value.status]), [["a", "ok"], ["b", "ok"], ["c", "in_use"], ["d", "failed"]]);
  assert.deepEqual(removalCounts(merged), { removed: 2, dirty: 0, kept: 2 });
  assert.deepEqual(mergeOutcomes([], [outcome(a, "ok")]).map(value => value.row.name), ["a"]);

  assert.deepEqual(removalChunks([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.deepEqual(removalChunks([], 2), []);

  assert.equal(removalReason("in_use", null, t), "A session is working there. Wait for it to finish, or stop it.");
  assert.equal(removalReason("dirty", null, t), "The worktree holds changes that are not committed.");
  assert.equal(removalReason("locked", null, t), "Git keeps this worktree locked: unlock it with git to remove it.");
  assert.equal(removalReason("main", null, t), "This is the folder of the project: it is not a worktree to remove.");
  assert.equal(removalReason("canceled", null, t), "Not started.");
  assert.equal(removalReason("unconfirmed", null, t), "The answer did not arrive: the list says whether it is still there.");
  assert.equal(removalReason("failed", "fatal: unable to remove", t), "fatal: unable to remove");
  assert.equal(removalReason("failed", null, t), "Git could not do it.");
});

test("what cannot be listed or opened is told in the words of the window", () => {
  assert.equal(t(inventoryFailure("not_repository")), "This folder is not in a git repository.");
  assert.equal(t(inventoryFailure("stale_epoch")), "The host changed. Reload the window.");
  assert.equal(t(inventoryFailure("read_failed")), "The worktrees could not be listed.");
  assert.equal(editorFailure("worktree_missing", t), "The folder of this worktree is gone.");
  assert.equal(editorFailure("not_worktree", t), "The worktree is no longer there.");
  assert.equal(editorFailure("no_window", t), "The code editor could not be opened.");
  assert.equal(editorFailure("failed", t), "Git could not do it.");
});

test("every word of the window is in the five languages", async () => {
  const { worktreeMessages } = await import("./messages");
  for (const [key, translations] of Object.entries(worktreeMessages)) {
    assert.equal(translations.length, 5, key);
    for (const [index, value] of translations.entries()) {
      assert.ok(value.trim().length > 0, `${key} [${index}]`);
      // What a sentence names is named in every language.
      assert.deepEqual((value.match(/\{[a-z]+\}/gu) ?? []).sort(), (key.match(/\{[a-z]+\}/gu) ?? []).sort(), `${key} [${index}]`);
    }
  }
});
