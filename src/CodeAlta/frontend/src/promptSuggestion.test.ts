import assert from "node:assert/strict";
import test from "node:test";
import type { SessionRuntimeStateEntry, SessionRuntimeStateResponse } from "#neoastra";
import { createAnsweredSuggestions, entryPromptSuggestion, observedPromptSuggestion, shownPromptSuggestion, takesPromptSuggestion } from "./promptSuggestion";

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

test("a suggestion names the run it answers, from the entry or, for a session without one, from what its journal keeps", () => {
  const state = (values: Partial<SessionRuntimeStateResponse>): SessionRuntimeStateResponse => ({
    status: "ok", hostEpoch: "epoch", sessionId: "s", runtimeInstanceId: "r", coordinatorTransitionInProgress: false, entry: null, ...values,
  } as SessionRuntimeStateResponse);
  assert.deepEqual(observedPromptSuggestion(state({ entry: entry({ promptSuggestion: "commit it", promptSuggestionRunId: "run-1" }) })),
    { text: "commit it", runId: "run-1" });
  assert.deepEqual(observedPromptSuggestion(state({ entry: entry({ promptSuggestion: "commit it" }) })), { text: "commit it", runId: null });
  assert.equal(observedPromptSuggestion(state({ entry: entry({ promptSuggestion: "commit it", activeRunId: "run-2" }) })), null);
  // The entry speaks for an attached session: what the journal keeps is only for a session without one.
  assert.equal(observedPromptSuggestion(state({ entry: entry({}), promptSuggestion: "commit it" })), null);
  assert.deepEqual(observedPromptSuggestion(state({ promptSuggestion: " commit it ", promptSuggestionRunId: "run-1" })), { text: "commit it", runId: "run-1" });
  assert.equal(observedPromptSuggestion(state({ promptSuggestion: "one\ntwo" })), null);
  assert.equal(observedPromptSuggestion(state({})), null);
  assert.equal(observedPromptSuggestion(null), null);
});

test("the same suggestion of a later run is shown after a prompt was sent after the earlier one", () => {
  const idle = { text: "", images: 0, busy: false };
  const first = { text: "commit it", runId: "run-1" };
  // The panel may not have watched the run in between: nothing but the run tells the two apart.
  assert.equal(shownPromptSuggestion(first, { ...idle, answered: first }), null);
  assert.equal(shownPromptSuggestion({ text: "commit it", runId: "run-2" }, { ...idle, answered: first }), "commit it");
  // Without a run on either side, the text decides, as before.
  assert.equal(shownPromptSuggestion({ text: "commit it", runId: null }, { ...idle, answered: first }), null);
  assert.equal(shownPromptSuggestion(first, { ...idle, answered: { text: "commit it", runId: null } }), null);
  assert.equal(shownPromptSuggestion(first, { ...idle, answered: "commit it" }), null);
  assert.equal(shownPromptSuggestion(first, { ...idle, answered: { text: "yes", runId: null } }), "commit it");
});

test("a prompt answers the suggestion shown when it was sent, not one a fast run made before the host accepted it", () => {
  const marks = createAnsweredSuggestions();
  const shown = { text: "commit it", runId: "run-1" };
  marks.capture("send-1", shown);
  // The run of that prompt ended and suggested something new before the answer of the host came: that one stays shown.
  const later = { text: "push it", runId: "run-2" };
  const answered = marks.accepted("send-1");
  assert.deepEqual(answered, shown);
  assert.equal(shownPromptSuggestion(later, { text: "", images: 0, busy: false, answered }), "push it");
  // Accepted once: a replay of the same answer marks nothing more.
  assert.equal(marks.accepted("send-1"), null);

  // A request sent again keeps what it was first sent after.
  marks.capture("send-2", shown);
  marks.capture("send-2", later);
  assert.deepEqual(marks.accepted("send-2"), shown);
  // Nothing shown, or a request not known: nothing is marked.
  marks.capture("send-3", null);
  assert.equal(marks.accepted("send-3"), null);
  assert.equal(marks.accepted("unknown"), null);

  // Bounded: the oldest are forgotten.
  const few = createAnsweredSuggestions(2);
  few.capture("a", shown); few.capture("b", shown); few.capture("c", later);
  assert.equal(few.accepted("a"), null);
  assert.deepEqual(few.accepted("c"), later);
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
