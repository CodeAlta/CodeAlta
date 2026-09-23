import test from "node:test";
import assert from "node:assert/strict";
import { createDraftIndicators, draftStorageKey, persistDraft, restoreDraft } from "./promptDraft";

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

test("edited indicator follows live edits, successful persistence, clearing and selection switches", () => {
  const indicators = createDraftIndicators();
  const values = new Map<string, string>();
  const observed: number[] = [];
  const unsubscribe = indicators.subscribe(() => observed.push(indicators.snapshot()));
  indicators.edit("one", "draft", "");
  assert.equal(indicators.visible("one", "one"), true);
  assert.equal(indicators.visible("one", "two"), false);
  indicators.persisted("one", persistDraft((key, value) => values.set(key, value), key => { values.delete(key); }, "one", "draft"));
  assert.equal(indicators.visible("one", "two"), true);
  indicators.edit("two", "second", "");
  assert.equal(indicators.visible("two", "two"), true);
  indicators.clear("one"); // Accepted Send/clear; no stale badge after switching back.
  assert.equal(indicators.visible("one", "one"), false);
  indicators.clear("two"); // Remounted composer restores text, not an edited state.
  assert.equal(restoreDraft(key => values.get(key) ?? null, "one"), "draft");
  assert.equal(indicators.visible("two", "two"), false);
  unsubscribe();
  assert.ok(observed.length >= 4);
});

test("restored, invalid, whitespace, oversized and uncertain retained drafts never receive a badge", () => {
  const indicators = createDraftIndicators();
  indicators.edit("one", "restored", "restored");
  indicators.edit("one", " \n\u0085", "");
  indicators.edit("one", "\ud800", "");
  indicators.edit("one", "x".repeat(32769), "");
  assert.equal(indicators.visible("one", "one"), false);
  indicators.edit("one", "change", "restored");
  indicators.persisted("one", persistDraft(() => { throw new Error("quota"); }, () => {}, "one", "change"));
  assert.equal(restoreDraft(() => { throw new Error("blocked"); }, "one"), "");
  assert.equal(indicators.visible("one", "one"), true);
  assert.equal(indicators.visible("one", "other"), false);
  indicators.clear("one"); // Send latched; uncertain retained text belongs to the pending operation.
  assert.equal(indicators.visible("one", "one"), false);
  indicators.edit("one", "new edit", "restored");
  indicators.persisted("one", true);
  indicators.edit("one", "restored", "restored");
  assert.equal(indicators.visible("one", "other"), false);
});
