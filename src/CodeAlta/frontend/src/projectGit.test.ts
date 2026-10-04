import assert from "node:assert/strict";
import test from "node:test";
import { projectGitStatus } from "./projectGit";

test("git facts are shown only from a well-formed answer for the asked project", () => {
  const reply = { status: "ok", projectId: "p", branch: "main", detached: false, insertions: 12, deletions: 3, changedFiles: 2 };
  assert.deepEqual(projectGitStatus(reply, "p"), { branch: "main", detached: false, changes: { insertions: 12, deletions: 3, files: 2 } });
  assert.deepEqual(projectGitStatus({ ...reply, branch: "abc1234", detached: true, insertions: null, deletions: null, changedFiles: null }, "p"),
    { branch: "abc1234", detached: true, changes: null }, "git missing or timed out: the branch alone");
  for (const bad of [null, "ok", { ...reply, status: "not_repository" }, { ...reply, projectId: "other" }, { ...reply, branch: "" },
    { ...reply, branch: 7 }, { ...reply, detached: "no" }]) assert.equal(projectGitStatus(bad, "p"), null);
  assert.equal(projectGitStatus({ ...reply, insertions: -1 }, "p")?.changes, null);
});
