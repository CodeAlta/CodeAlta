import assert from "node:assert/strict";
import test from "node:test";
import { approvalKey, approvalMarkKey, approvalQuestion, approvalSubject, approvalWaiting } from "./approval";
import type { ApprovalRequest, Live } from "./contract";

const command: ApprovalRequest = { id: "r1", kind: "commandExecution", command: "git push", workingDirectory: "C:/code", grantRoot: null, reason: null, shortened: false, outsideSandbox: false, skill: null };
const files: ApprovalRequest = { ...command, id: "r2", kind: "fileChange", command: null, workingDirectory: null, grantRoot: "C:/code", reason: "via Edit: src/a.ts" };
const skill: ApprovalRequest = { ...command, id: "r3", kind: "skill", command: null, workingDirectory: null, skill: { server: "hf", name: "transformers" } };
const live = (approving: Live["approving"]): Live => ({ id: "s", live: "running", kind: null, line: null, at: null, activity: null, canSteer: true, approving });

test("a session waits while it has a request, also while its turn runs", () => {
  assert.equal(approvalWaiting(live({ id: "r1", count: 1 })), true);
  assert.equal(approvalWaiting(live(null)), false);
  assert.equal(approvalWaiting(live(undefined)), false, "an older CodeAlta does not say");
  assert.equal(approvalWaiting(null), false);
});

test("the requests are read again when the first one or their number changes", () => {
  const key = approvalMarkKey({ id: "r1", count: 1 });
  assert.equal(approvalMarkKey({ id: "r1", count: 1 }), key);
  assert.notEqual(approvalMarkKey({ id: "r2", count: 1 }), key);
  assert.notEqual(approvalMarkKey({ id: "r1", count: 2 }), key);
  assert.equal(approvalMarkKey(null), "");
});

test("a card says what is asked, for each kind of request", () => {
  assert.equal(approvalQuestion(command), "Allow this command?");
  assert.equal(approvalSubject(command), "git push");
  assert.equal(approvalQuestion(files), "Allow file changes under this folder?");
  assert.equal(approvalSubject(files), "C:/code");
  assert.equal(approvalQuestion(skill), "Load this skill?");
  assert.equal(approvalSubject(skill), "transformers (hf)");
});

test("a card is drawn again only when what it shows changes", () => {
  const key = approvalKey(command, false, false, true, "");
  assert.equal(approvalKey({ ...command }, false, false, true, ""), key);
  for (const other of [approvalKey({ ...command, command: "git push --force" }, false, false, true, ""), approvalKey(command, true, false, true, ""),
    approvalKey(command, false, true, true, ""), approvalKey(command, false, false, false, ""), approvalKey(command, false, false, true, "Refused.")])
    assert.notEqual(other, key);
});
