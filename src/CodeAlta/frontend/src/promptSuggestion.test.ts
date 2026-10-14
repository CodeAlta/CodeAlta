import assert from "node:assert/strict";
import test from "node:test";
import type { SessionRuntimeStateEntry } from "#neoastra";
import { entryPromptSuggestion, shownPromptSuggestion, takesPromptSuggestion } from "./promptSuggestion";

const entry = (values: Partial<SessionRuntimeStateEntry>): SessionRuntimeStateEntry => ({
  attachmentGeneration: "1", isTerminated: false, isRetiring: false, activeRunId: null, queueDrainInProgress: false,
  providerId: "claude-code", providerKey: "claude", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null,
  backgroundTasks: [], ...values,
} as SessionRuntimeStateEntry);

test("a suggestion is the one line the host says, once no run goes on", () => {
  assert.equal(entryPromptSuggestion(entry({ promptSuggestion: " go ahead with rope " })), "go ahead with rope");
  assert.equal(entryPromptSuggestion(entry({})), null);
  assert.equal(entryPromptSuggestion(null), null);
  // A run that goes on, and an attachment that ends, show none.
  assert.equal(entryPromptSuggestion(entry({ promptSuggestion: "yes", activeRunId: "run-1" })), null);
  assert.equal(entryPromptSuggestion(entry({ promptSuggestion: "yes", isRetiring: true })), null);
  assert.equal(entryPromptSuggestion(entry({ promptSuggestion: "yes", isTerminated: true })), null);
  // What is not one line of text is not a prompt to take with a key.
  for (const value of ["", "   ", "one\ntwo", "x".repeat(257), 12, {}])
    assert.equal(entryPromptSuggestion(entry({ promptSuggestion: value as string })), null);
});

test("a suggestion is shown whenever the prompt is empty and not being sent, until a prompt was sent after it", () => {
  const idle = { text: "", images: 0, busy: false, answered: null };
  assert.equal(shownPromptSuggestion("yes", idle), "yes");
  assert.equal(shownPromptSuggestion(null, idle), null);
  assert.equal(shownPromptSuggestion("yes", { ...idle, text: "n" }), null);
  assert.equal(shownPromptSuggestion("yes", { ...idle, images: 1 }), null);
  assert.equal(shownPromptSuggestion("yes", { ...idle, busy: true }), null);
  // Written over and emptied again, the prompt shows it again: nothing remembers that something else was written.
  // The one a prompt was sent after is over, whatever the host still says; the next one is shown.
  assert.equal(shownPromptSuggestion("yes", { ...idle, answered: "yes" }), null);
  assert.equal(shownPromptSuggestion("run the tests", { ...idle, answered: "yes" }), "run the tests");
});

test("Tab and the right arrow take a suggestion, alone", () => {
  const key = { key: "Tab", ctrlKey: false, shiftKey: false, altKey: false, metaKey: false, isComposing: false, keyCode: 9 };
  assert.equal(takesPromptSuggestion(key), true);
  assert.equal(takesPromptSuggestion({ ...key, key: "ArrowRight", keyCode: 39 }), true);
  assert.equal(takesPromptSuggestion({ ...key, key: "Enter", keyCode: 13 }), false);
  assert.equal(takesPromptSuggestion({ ...key, shiftKey: true }), false);
  assert.equal(takesPromptSuggestion({ ...key, ctrlKey: true }), false);
  assert.equal(takesPromptSuggestion({ ...key, key: "ArrowRight", altKey: true }), false);
  assert.equal(takesPromptSuggestion({ ...key, isComposing: true }), false);
  assert.equal(takesPromptSuggestion({ ...key, keyCode: 229 }), false);
});
