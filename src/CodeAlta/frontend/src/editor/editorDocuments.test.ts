import assert from "node:assert/strict";
import test from "node:test";
import { canSaveDocument, documentChecked, documentConflictDismissed, documentEdited, documentImageRead, documentKind, documentPreview,
  documentRead, documentSaved, documentSaveUnknown, documentSaving, documentStatus, documentTooLarge, formatFileSize, newDocument } from "./editorDocuments";

const read = (content: string, revision: string, stamp: string, readOnly = false) =>
  ({ status: "ok", content, revision, readOnly, stamp, encoding: "UTF-8", length: content.length });
const failed = (status: string) => ({ status, content: null, revision: null, readOnly: false, stamp: null, encoding: null, length: 0 });
const ready = () => documentRead(newDocument("src/a.ts"), read("one", "r1", "s1"));

test("the extension decides how a file is shown", () => {
  for (const path of ["a.png", "img/B.JPG", "c.jpeg", "d.gif", "e.webp", "f.bmp", "g.ico", "h.avif"]) assert.equal(documentKind(path), "image", path);
  for (const path of ["a.ts", "logo.svg", "readme.md", "png", ".png", "folder.png/file"]) assert.equal(documentKind(path), "text", path);
  assert.deepEqual([documentPreview("a/logo.SVG"), documentPreview("README.md"), documentPreview("notes.markdown"), documentPreview("a.ts")], ["svg", "markdown", "markdown", null]);
  assert.equal(documentPreview("site/index.html"), null);
});

test("a first read shows the file, or why it cannot be shown", () => {
  const document = ready();
  assert.deepEqual([document.phase, document.text, document.generation, document.revision, document.stamp, document.encoding, document.length],
    ["ready", "one", 1, "r1", "s1", "UTF-8", 3]);
  assert.equal(documentStatus(newDocument("a.ts")), "Loading…");
  assert.equal(documentStatus(document), "Saved");
  assert.equal(documentStatus(documentRead(newDocument("a.ts"), read("x", "r", "s", true))), "Read-only");
  const binary = documentRead(newDocument("a.ts"), failed("binary"));
  assert.deepEqual([binary.phase, binary.failure, documentStatus(binary)], ["failed", "This file is not text and cannot be edited here.", "Unavailable"]);
  assert.equal(documentRead(newDocument("a.ts"), { ...read("x", "r", "s"), content: null }).failure, "The file could not be read.");
});

test("a file that changed on the disk is read again when it holds no edit, and is in conflict when it does", () => {
  const document = ready();
  assert.equal(documentChecked(document, { status: "ok", stamp: "s1", readOnly: false }), document);
  assert.equal(documentChecked(document, undefined), document);
  assert.equal(documentChecked(document, { status: "read_failed", stamp: null, readOnly: false }), document);

  // Without edits: read again, and the new text replaces the old one.
  const changed = documentChecked(document, { status: "ok", stamp: "s2", readOnly: false });
  assert.deepEqual([changed.reloading, changed.conflict, changed.stamp], [true, false, "s1"]);
  assert.equal(documentChecked(changed, { status: "ok", stamp: "s3", readOnly: false }), changed, "One read at a time.");
  const reloaded = documentRead(changed, read("two", "r2", "s2"));
  assert.deepEqual([reloaded.text, reloaded.generation, reloaded.reloading, reloaded.stamp], ["two", 2, false, "s2"]);
  // Touched without another content: nothing is replaced.
  const touched = documentRead(changed, read("one", "r1", "s2"));
  assert.deepEqual([touched.text, touched.generation, touched.stamp], ["one", 1, "s2"]);

  // Edited while it was being read again: nothing typed is replaced.
  const typed = documentRead(documentEdited(changed, true), read("two", "r2", "s2"));
  assert.deepEqual([typed.text, typed.generation, typed.dirty, typed.conflict, typed.reloading, typed.stamp], ["one", 1, true, true, false, "s2"]);

  // A reload that was asked for replaces what was typed, also when the disk still holds what was read.
  const discarded = documentRead(documentEdited(document, true), read("one", "r1", "s1"), true);
  assert.deepEqual([discarded.text, discarded.generation, discarded.dirty, discarded.conflict], ["one", 2, false, false]);
  const replaced = documentRead(documentEdited(changed, true), read("two", "r2", "s2"), true);
  assert.deepEqual([replaced.text, replaced.generation, replaced.dirty, replaced.conflict], ["two", 2, false, false]);
  // One that fails changes nothing of what is held.
  const kept = documentRead({ ...documentEdited(document, true), reloading: true }, failed("read_failed"), true);
  assert.deepEqual([kept.text, kept.dirty, kept.revision, kept.reloading], ["one", true, "r1", false]);

  // With edits: a conflict, flagged once for each change of the file.
  const dirty = documentEdited(document, true);
  const conflict = documentChecked(dirty, { status: "ok", stamp: "s2", readOnly: false });
  assert.deepEqual([conflict.conflict, conflict.reloading, conflict.dirty, conflict.stamp, documentStatus(conflict)], [true, false, true, "s2", "Changed on disk"]);
  const dismissed = documentConflictDismissed(conflict);
  assert.equal(documentChecked(dismissed, { status: "ok", stamp: "s2", readOnly: false }), dismissed);
  assert.equal(documentChecked(dismissed, { status: "ok", stamp: "s3", readOnly: false }).conflict, true);
  assert.equal(documentConflictDismissed(document), document);
});

test("a file deleted on the disk keeps its text and comes back when the file does", () => {
  const document = ready();
  const missing = documentChecked(document, { status: "not_found", stamp: null, readOnly: false });
  assert.deepEqual([missing.missing, missing.text, documentStatus(missing)], [true, "one", "Deleted on disk"]);
  assert.equal(documentChecked(missing, { status: "not_found", stamp: null, readOnly: false }), missing);
  assert.equal(documentChecked(missing, { status: "ok", stamp: "s1", readOnly: false }).missing, false);
  // A reload that finds no file marks it too, without dropping the text.
  const reloading = documentChecked(document, { status: "ok", stamp: "s2", readOnly: false });
  const gone = documentRead(reloading, failed("not_found"));
  assert.deepEqual([gone.phase, gone.missing, gone.reloading, gone.text], ["ready", true, false, "one"]);
  assert.deepEqual([documentRead(reloading, failed("read_failed")).missing, documentRead(reloading, failed("read_failed")).reloading], [false, false]);
  assert.equal(documentChecked(document, { status: "ok", stamp: "s1", readOnly: true }).readOnly, true);
});

test("a save is sent for edits of a writable file, and only an accepted one moves what the disk holds", () => {
  const document = ready();
  assert.equal(canSaveDocument(document), false);
  const dirty = documentEdited(document, true);
  assert.equal(documentEdited(dirty, true), dirty);
  assert.deepEqual([canSaveDocument(dirty), canSaveDocument(dirty, true), documentStatus(dirty)], [true, false, "Modified"]);
  const saving = documentSaving(dirty);
  assert.deepEqual([canSaveDocument(saving), documentStatus(saving)], [false, "Saving…"]);
  assert.equal(documentChecked(saving, { status: "ok", stamp: "s9", readOnly: false }), saving, "Its own write is not a change from elsewhere.");

  const saved = documentSaved(saving, { status: "ok", revision: "r2", stamp: "s2" });
  assert.deepEqual([saved.saving, saved.revision, saved.stamp, saved.notice], [false, "r2", "s2", null]);
  assert.equal(documentSaved(saving, { status: "ok", revision: null, stamp: null }).notice?.intent, "danger");
  const conflict = documentSaved(saving, { status: "conflict", revision: "r9", stamp: null });
  assert.deepEqual([conflict.conflict, conflict.revision, canSaveDocument(conflict, true)], [true, "r1", true]);
  const readOnly = documentSaved(saving, { status: "read_only", revision: "r1", stamp: null });
  assert.deepEqual([readOnly.readOnly, readOnly.notice?.key, canSaveDocument(readOnly)], [true, "The file is read-only; nothing was saved.", false]);
  assert.deepEqual([documentSaved(saving, { status: "not_found", revision: null, stamp: null }).missing, documentSaved(saving, { status: "write_failed", revision: null, stamp: null }).notice?.key],
    [true, "The file could not be written; nothing was saved."]);
  for (const status of ["too_large", "binary", "archived_project", "stale_epoch", "unavailable"])
    assert.ok(documentSaved(saving, { status, revision: null, stamp: null }).notice, status);
  assert.deepEqual([documentSaveUnknown(saving).saving, documentSaveUnknown(saving).notice?.intent], [false, "danger"]);
  assert.deepEqual([documentTooLarge("x".repeat(1024 * 1024)), documentTooLarge("x".repeat(1024 * 1024 + 1))], [false, true]);
  // Nothing to save in a picture, a file being loaded or one that failed.
  assert.equal(canSaveDocument({ ...dirty, kind: "image" }), false);
  assert.equal(documentEdited(newDocument("a.ts"), true).dirty, false);
});

test("a picture is shown from the bytes that were read, and read again when its file changes", () => {
  const image = documentImageRead(newDocument("logo.png"), { status: "ok", mediaType: "image/png", stamp: "s1", length: 2048 }, "blob:1");
  assert.deepEqual([image.kind, image.phase, image.image, image.length, image.generation], ["image", "ready", { url: "blob:1", mediaType: "image/png" }, 2048, 1]);
  const changed = documentChecked(image, { status: "ok", stamp: "s2", readOnly: true });
  assert.deepEqual([changed.reloading, changed.readOnly], [true, false]);
  const again = documentImageRead(changed, { status: "ok", mediaType: "image/png", stamp: "s2", length: 4096 }, "blob:2");
  assert.deepEqual([again.image?.url, again.reloading, again.generation], ["blob:2", false, 2]);
  assert.deepEqual([documentImageRead(changed, { status: "not_found", mediaType: null, stamp: null, length: 0 }, null).missing,
    documentImageRead(changed, { status: "read_failed", mediaType: null, stamp: null, length: 0 }, null).image?.url], [true, "blob:1"]);
  assert.equal(documentImageRead(newDocument("a.png"), { status: "too_large", mediaType: null, stamp: null, length: 0 }, null).failure,
    "This image is larger than 16 MiB and cannot be shown here.");
  assert.equal(documentImageRead(newDocument("a.png"), { status: "unsupported_type", mediaType: null, stamp: null, length: 0 }, null).failure,
    "This file is not an image the editor can show.");
  assert.equal(documentImageRead(newDocument("a.png"), { status: "ok", mediaType: "image/png", stamp: "s", length: 1 }, null).phase, "failed");
});

test("sizes are short", () => {
  assert.deepEqual([formatFileSize(0, "en"), formatFileSize(812, "en"), formatFileSize(1536, "en"), formatFileSize(14540, "en"), formatFileSize(3250586, "en")],
    ["0 B", "812 B", "1.5 KB", "14 KB", "3.1 MB"]);
});
