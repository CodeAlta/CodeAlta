import test from "node:test";
import assert from "node:assert/strict";
import { draftStorageKey, persistDraft, restoreDraft } from "./promptDraft";

test("prompt drafts are session-scoped, bounded, removable, and tolerate storage failure", () => {
  const values = new Map<string, string>();
  assert.equal(persistDraft((key, value) => values.set(key, value), key => { values.delete(key); }, "session", "draft"), true);
  assert.equal(values.get(draftStorageKey("session")), "draft");
  assert.equal(restoreDraft(key => values.get(key) ?? null, "session"), "draft");
  assert.equal(persistDraft((key, value) => values.set(key, value), key => { values.delete(key); }, "session", ""), true);
  assert.equal(values.has(draftStorageKey("session")), false);
  assert.equal(restoreDraft(() => { throw new Error("blocked"); }, "session"), "");
  assert.equal(persistDraft(() => { throw new Error("full"); }, () => {}, "session", "draft"), false);
});
