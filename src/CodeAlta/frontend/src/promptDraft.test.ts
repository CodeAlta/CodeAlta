import test from "node:test";
import assert from "node:assert/strict";
import { createDraftIndicators, createLocalDrafts, draftSendFacts, draftStorageKey, persistDraft, restoreDraft, transferPromptDraft } from "./promptDraft";

test("explicit draft transfer requires an empty readable destination and exact readback", () => {
  const values = new Map<string, string>();
  const read = (key: string) => values.get(key) ?? null;
  const write = (key: string, text: string) => { values.set(key, text); };
  assert.equal(transferPromptDraft(read, write, "created", "  exact\ntext  "), true);
  assert.equal(read(draftStorageKey("created")), "  exact\ntext  ");
  assert.equal(transferPromptDraft(read, write, "created", "newer"), false);
  assert.equal(transferPromptDraft(() => { throw Error("denied"); }, write, "unknown", "original"), false);
  assert.equal(values.has(draftStorageKey("unknown")), false);
  assert.equal(transferPromptDraft(read, () => { throw Error("quota"); }, "empty", "original"), false);
  assert.equal(transferPromptDraft(read, () => {}, "empty", "original"), false);
  assert.equal(transferPromptDraft(read, write, "empty", "x".repeat(32769)), false);
});

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
  const first = indicators.edit("one", "draft", "");
  assert.equal(indicators.visible("one", "one"), true);
  assert.equal(indicators.visible("one", "two"), false);
  indicators.persisted("one", first, persistDraft((key, value) => values.set(key, value), key => { values.delete(key); }, "one", "draft"));
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
  const changed = indicators.edit("one", "change", "restored");
  indicators.persisted("one", changed, persistDraft(() => { throw new Error("quota"); }, () => {}, "one", "change"));
  assert.equal(restoreDraft(() => { throw new Error("blocked"); }, "one"), "");
  assert.equal(indicators.visible("one", "one"), true);
  assert.equal(indicators.visible("one", "other"), false);
  indicators.clear("one"); // Send latched; uncertain retained text belongs to the pending operation.
  assert.equal(indicators.visible("one", "one"), false);
  const renewed = indicators.edit("one", "new edit", "restored");
  indicators.persisted("one", renewed, true);
  indicators.edit("one", "restored", "restored");
  assert.equal(indicators.visible("one", "other"), false);
});

test("stale persistence success cannot certify a newer edit across selection switches", () => {
  const indicators = createDraftIndicators();
  const first = indicators.edit("session", "first", "");
  const second = indicators.edit("session", "second", "");
  indicators.persisted("session", first, true); // Old first-write completion.
  assert.equal(indicators.visible("session", "other"), false);
  assert.equal(indicators.visible("session", "session"), true);
  const other = indicators.edit("other", "other edit", "");
  indicators.persisted("other", second, true); // Correct generation but wrong session.
  assert.equal(indicators.visible("other", "session"), false);
  indicators.persisted("other", other, true);
  assert.equal(indicators.visible("other", "session"), true);
  indicators.persisted("session", second, true);
  assert.equal(indicators.visible("session", "other"), true);
});

test("stale persistence failure cannot revoke a newer confirmed edit or revive a remounted editor", () => {
  const indicators = createDraftIndicators();
  const first = indicators.edit("session", "first", "");
  const second = indicators.edit("session", "second", "");
  indicators.persisted("session", second, true); // New second-write completion.
  indicators.persisted("session", first, false); // Old first-write completion.
  assert.equal(indicators.visible("session", "other"), true);
  indicators.clear("session"); // Remount or retained Send; an old callback cannot revive it.
  indicators.persisted("session", second, true);
  assert.equal(indicators.visible("session", "session"), false);
  const remounted = indicators.edit("session", "new", "");
  indicators.persisted("session", second, true);
  assert.equal(indicators.visible("session", "other"), false);
  indicators.persisted("session", remounted, true);
  assert.equal(indicators.visible("session", "other"), true);
});

test("the draft of a session that does not exist yet is restored once, and each edit is another revision", () => {
  const drafts = createLocalDrafts();
  let restored = 0;
  const restore = () => { restored++; return "kept"; };
  assert.equal(drafts.peek("project"), undefined);
  assert.deepEqual(drafts.get("project", restore), { text: "kept", revision: 0 });
  assert.equal(drafts.get("project", restore), drafts.peek("project"));
  assert.equal(restored, 1);
  const seen: string[] = [];
  const unsubscribe = drafts.subscribe(() => seen.push(drafts.peek("project")!.text));
  drafts.edit("project", "kept and more");
  // The same text typed again (a selection replaced by itself) is still an edit: a hand-off of the earlier one is refused.
  drafts.edit("project", "kept and more");
  assert.deepEqual(drafts.peek("project"), { text: "kept and more", revision: 2 });
  assert.deepEqual(seen, ["kept and more", "kept and more"]);
  // Another scope keeps its own text.
  assert.deepEqual(drafts.get("chats", () => ""), { text: "", revision: 0 });
  unsubscribe();
  drafts.edit("project", "");
  assert.equal(seen.length, 2);
  assert.deepEqual(drafts.peek("project"), { text: "", revision: 3 });
});

test("typing changes what the window follows of a draft only when its sending does", () => {
  const facts = (text: string) => draftSendFacts(text, 8);
  // What App reads at each keystroke: it is rendered again only when the number differs.
  const typed = ["h", "he", "hel", "hello"].map(facts);
  assert.ok(typed.every(value => value === typed[0]));
  assert.notEqual(facts(""), facts("h"));
  assert.notEqual(facts(" "), facts(""));
  assert.notEqual(facts(" "), facts("h"));
  assert.equal(facts(" \n\t"), facts(" "));
  // The limit of a prompt with images counts too.
  assert.equal(facts("12345678"), facts("h"));
  assert.notEqual(facts("123456789"), facts("12345678"));
});
