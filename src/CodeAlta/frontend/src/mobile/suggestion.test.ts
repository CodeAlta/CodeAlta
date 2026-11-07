import assert from "node:assert/strict";
import test from "node:test";
import type { Live } from "./contract";
import { phoneSuggestion } from "./suggestion";

const live = (change: Partial<Live> = {}): Live => ({ id: "s1", live: "idle", kind: "assistant", line: null, at: null, activity: null, canSteer: false, suggestion: " go ahead ", ...change });
const empty = { text: "", files: 0, waiting: false, answered: null };

test("an idle session offers its suggestion over an empty box, trimmed", () => {
  assert.equal(phoneSuggestion(live(), empty), "go ahead");
});

test("no suggestion while a turn runs, something waits for the person, or the session suggests none", () => {
  for (const state of ["running", "tools", "thinking"] as const) assert.equal(phoneSuggestion(live({ live: state }), empty), null, state);
  assert.equal(phoneSuggestion(live(), { ...empty, waiting: true }), null);
  assert.equal(phoneSuggestion(live({ suggestion: null }), empty), null);
  assert.equal(phoneSuggestion(live({ suggestion: undefined }), empty), null, "an older CodeAlta does not say");
  assert.equal(phoneSuggestion(null, empty), null);
});

test("what is not one line of a prompt is not offered", () => {
  assert.equal(phoneSuggestion(live({ suggestion: "two\nlines" }), empty), null);
  assert.equal(phoneSuggestion(live({ suggestion: "a".repeat(257) }), empty), null);
  assert.equal(phoneSuggestion(live({ suggestion: "   " }), empty), null);
});

test("text or files in the box hide it, and emptying the box brings it back", () => {
  assert.equal(phoneSuggestion(live(), { ...empty, text: "r" }), null);
  assert.equal(phoneSuggestion(live(), { ...empty, text: " " }), null, "anything typed hides it");
  assert.equal(phoneSuggestion(live(), { ...empty, files: 1 }), null);
  assert.equal(phoneSuggestion(live(), empty), "go ahead");
});

test("the suggestion a prompt was sent after is not offered again; another one is", () => {
  assert.equal(phoneSuggestion(live(), { ...empty, answered: "go ahead" }), null);
  assert.equal(phoneSuggestion(live({ suggestion: "run the tests" }), { ...empty, answered: "go ahead" }), "run the tests");
});
