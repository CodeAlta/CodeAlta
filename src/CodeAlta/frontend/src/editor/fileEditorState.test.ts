import assert from "node:assert/strict";
import test from "node:test";
import { canSaveFile, fileConflictDismissed, fileEdited, fileLoaded, fileLoading, fileReadFailure, fileSaved, fileSaveUnknown, fileSaving, fileStatus,
  initialFileEditorState, maximumFileLength } from "./fileEditorState";
import { messages } from "../localization";

const read = (content: string, readOnly = false) => fileLoaded({ status: "ok", content, revision: "r1", readOnly });

test("a read shows the text clean; refusals name why the file cannot be shown", () => {
  assert.equal(fileStatus(initialFileEditorState), "Loading…");
  const ready = read("one\n");
  assert.deepEqual([ready.phase, ready.content, ready.dirty, ready.loading, ready.baseline?.revision], ["ready", "one\n", false, false, "r1"]);
  assert.equal(fileStatus(ready), "Saved");
  assert.equal(fileStatus(read("x", true)), "Read-only");
  for (const [status, key] of [["binary", "This file is not text and cannot be edited here."],
    ["too_large", "This file is larger than 1 MiB and cannot be edited here."], ["not_found", "This file no longer exists."],
    ["outside_root", "This file is outside the project folder."], ["archived_project", "This project is archived; its files cannot be edited."],
    ["unknown_project", "The project folder is unavailable."], ["project_unavailable", "The project folder is unavailable."],
    ["stale_epoch", "The host changed. Reload the window to edit files."], ["unavailable", "File editing requires an owned host."],
    ["read_failed", "The file could not be read."], ["something_new", "The file could not be read."]] as const) {
    const failed = fileLoaded({ status, content: null, revision: null, readOnly: false });
    assert.deepEqual([failed.phase, failed.failure, failed.baseline, failed.loading], ["failed", key, null, false], status);
    assert.equal(fileReadFailure(status), key);
    assert.ok(key in messages);
    assert.equal(fileStatus(failed), "Unavailable");
    assert.equal(canSaveFile(failed), false);
  }
  // An answer without text or revision is not a usable read.
  assert.equal(fileLoaded({ status: "ok", content: "text", revision: null, readOnly: false }).phase, "failed");
});

test("edits mark the file modified; restoring the text or only changing line endings does not", () => {
  const ready = read("one\r\ntwo\n");
  assert.equal(fileEdited(ready, ready.content), ready);
  const edited = fileEdited(ready, "one\r\ntwo!\n");
  assert.equal(edited.dirty, true);
  assert.equal(fileStatus(edited), "Modified");
  assert.equal(canSaveFile(edited), true);
  assert.equal(fileEdited(edited, "one\r\ntwo\n").dirty, false);
  // Monaco shows a file with mixed line endings in its dominant one.
  const normalized = fileEdited(ready, "one\r\ntwo\r\n");
  assert.deepEqual([normalized.content, normalized.dirty], ["one\r\ntwo\r\n", false]);
  assert.equal(canSaveFile(normalized), false);
  assert.equal(fileEdited(initialFileEditorState, "typed before the read"), initialFileEditorState);
});

test("a save is offered only for changed, in-limit text of a writable file while nothing is in flight", () => {
  const edited = fileEdited(read("a"), "b");
  assert.equal(canSaveFile(read("a")), false);
  assert.equal(canSaveFile(fileEdited(read("a", true), "b")), false);
  assert.equal(canSaveFile(fileSaving(edited)), false);
  assert.equal(canSaveFile(fileLoading(edited)), false);
  assert.equal(canSaveFile(fileEdited(read("a"), "x".repeat(maximumFileLength + 1))), false);
  assert.equal(canSaveFile(fileEdited(read("a"), "x".repeat(maximumFileLength))), true);
  assert.equal(canSaveFile(edited, true), false);
  assert.equal(fileStatus(fileSaving(edited)), "Saving…");
});

test("a successful save moves the baseline to the submitted text and its new revision", () => {
  const saving = fileSaving(fileEdited(read("a"), "b"));
  const saved = fileSaved(saving, "b", { status: "ok", revision: "r2" });
  assert.deepEqual([saved.baseline?.content, saved.baseline?.revision, saved.dirty, saved.saving, saved.notice], ["b", "r2", false, false, null]);
  assert.equal(fileStatus(saved), "Saved");
  // Text typed while the save was in flight stays modified against what was written.
  const typed = fileSaved(fileEdited(saving, "bc"), "b", { status: "ok", revision: "r2" });
  assert.deepEqual([typed.content, typed.dirty, typed.baseline?.content], ["bc", true, "b"]);
  const unconfirmed = fileSaved(saving, "b", { status: "ok", revision: null });
  assert.deepEqual([unconfirmed.baseline?.revision, unconfirmed.dirty, unconfirmed.notice?.intent], ["r1", true, "danger"]);
});

test("a conflict keeps the edits and offers reload or overwrite; overwriting or dismissing resolves it", () => {
  const edited = fileEdited(read("a"), "mine");
  const conflict = fileSaved(fileSaving(edited), "mine", { status: "conflict", revision: "disk" });
  assert.deepEqual([conflict.conflict, conflict.content, conflict.dirty, conflict.saving, conflict.baseline?.revision], [true, "mine", true, false, "r1"]);
  assert.equal(fileStatus(conflict), "Changed on disk");
  assert.equal(canSaveFile(conflict, true), true);
  const overwritten = fileSaved(fileSaving(conflict), "mine", { status: "ok", revision: "r3" });
  assert.deepEqual([overwritten.conflict, overwritten.dirty, overwritten.baseline?.revision], [false, false, "r3"]);
  const dismissed = fileConflictDismissed(conflict);
  assert.deepEqual([dismissed.conflict, dismissed.dirty, dismissed.content], [false, true, "mine"]);
  assert.equal(fileConflictDismissed(edited), edited);
  // Reloading replaces the edits with what is on disk.
  const reloading = fileLoading(conflict);
  assert.deepEqual([reloading.phase, reloading.loading, reloading.content], ["ready", true, "mine"]);
  const reloaded = fileLoaded({ status: "ok", content: "theirs", revision: "disk", readOnly: false });
  assert.deepEqual([reloaded.content, reloaded.dirty, reloaded.conflict, reloaded.baseline?.revision], ["theirs", false, false, "disk"]);
});

test("refused and unanswered saves keep the edits and say what happened", () => {
  const saving = fileSaving(fileEdited(read("a"), "b"));
  for (const [status, key] of [["read_only", "The file is read-only; nothing was saved."], ["too_large", "The text is larger than 1 MiB; nothing was saved."],
    ["not_found", "This file no longer exists."], ["write_failed", "The file could not be written; nothing was saved."],
    ["outside_root", "The file could not be written; nothing was saved."], ["stale_epoch", "The host changed. Reload the window to edit files."],
    ["unavailable", "File editing requires an owned host."], ["archived_project", "This project is archived; its files cannot be edited."]] as const) {
    const refused = fileSaved(saving, "b", { status, revision: null });
    assert.deepEqual([refused.notice?.key, refused.dirty, refused.content, refused.saving, refused.baseline?.revision], [key, true, "b", false, "r1"], status);
    assert.ok(key in messages);
  }
  const readOnly = fileSaved(saving, "b", { status: "read_only", revision: "r1" });
  assert.equal(readOnly.readOnly, true);
  assert.equal(canSaveFile(readOnly), false);
  const unknown = fileSaveUnknown(saving);
  assert.deepEqual([unknown.saving, unknown.dirty, unknown.notice?.key], [false, true, "The save did not complete; reload to see what is on disk."]);
  // The next attempt clears the previous notice.
  assert.equal(fileSaving(unknown).notice, null);
});

test("the first read shows a loading state; a retry after a refusal starts over", () => {
  const failed = fileLoaded({ status: "not_found", content: null, revision: null, readOnly: false });
  const retry = fileLoading(failed);
  assert.deepEqual([retry.phase, retry.loading, retry.failure], ["loading", true, null]);
  assert.equal(fileStatus(retry), "Loading…");
});
