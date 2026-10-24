import assert from "node:assert/strict";
import test from "node:test";
import type { Live, TimelineEvent } from "./contract";
import { agentLabel, liveWord, modelLabel, safeColor, sessionMeta, showSpaces } from "./sessionMeta";
import type { SessionView } from "./sessionStore";
import { pendingGrace, stillPending, type PendingPrompt } from "./timeline";

const live = (state: Live["live"], kind: Live["kind"] = "assistant"): Live => ({ id: "s", projectId: "p", live: state, kind, line: null, at: null, activity: null, canSteer: false });
const view = (overrides: Partial<SessionView> = {}): SessionView => ({
  id: "s", title: "T", projectId: "p", project: "parser", space: { id: "work", name: "Work", color: "#2d72d2", icon: null },
  model: { provider: "codex", providerName: "Codex", id: "gpt-6.1-sol", name: "GPT-6.1 Sol", reasoning: "low" }, agent: { id: "default", name: "Default" },
  worktree: null, parentId: null, summary: null, lastActivity: null, live: live("idle"), ...overrides,
});

test("a word beside the dot only while the session works", () => {
  assert.deepEqual([liveWord(live("running")), liveWord(live("running", "writing")), liveWord(live("tools")), liveWord(live("thinking")), liveWord(live("idle")), liveWord(null)],
    ["Working", "Writing", "Tools", "Thinking", null, null]);
});

test("the model and its effort, as the desktop's composer says them", () => {
  assert.equal(modelLabel(view().model), "GPT-6.1 Sol · low");
  assert.equal(modelLabel({ provider: "claude", providerName: "Claude", id: "opus", name: null, reasoning: null }), "opus");
  assert.equal(modelLabel({ provider: "claude", providerName: "Claude", id: null, name: null, reasoning: null }), "Claude");
  assert.equal(modelLabel(null), null);
});

test("the agent prompt only when it is not the default one", () => {
  assert.equal(agentLabel({ id: "default", name: "Default" }), null);
  assert.equal(agentLabel({ id: "plan", name: "Plan" }), "Plan");
  assert.equal(agentLabel({ id: "review", name: null }), "review");
  assert.equal(agentLabel(null), null);
});

test("the space is named when the phone sees several (no list is every space)", () => {
  assert.equal(showSpaces([]), true);
  assert.equal(showSpaces([{ id: "a", name: "A", color: null, icon: null }]), false);
  assert.equal(showSpaces([{ id: "a", name: "A", color: null, icon: null }, { id: "b", name: "B", color: null, icon: null }]), true);
});

test("the facts of a session, in order, compact", () => {
  assert.deepEqual(sessionMeta(view(), { space: false, live: true }).map(item => `${item.kind}:${item.text}`), ["project:parser", "model:GPT-6.1 Sol · low"]);
  const busy = view({ live: live("tools"), worktree: "fix-quotes", parentId: "parent", agent: { id: "plan", name: "Plan" } });
  assert.deepEqual(sessionMeta(busy, { space: true, live: true }).map(item => `${item.kind}:${item.text}`),
    ["live:Tools", "space:Work", "project:parser", "worktree:fix-quotes", "model:GPT-6.1 Sol · low", "agent:Plan", "child:Sub-agent"]);
  assert.equal(sessionMeta(busy, { space: true, live: false })[0].kind, "space", "the page of a session has no live word");
});

test("only a hex color reaches a style", () => {
  assert.equal(safeColor("#2d72d2"), "#2d72d2");
  assert.equal(safeColor("red; background: url(x)"), null);
  assert.equal(safeColor(null), null);
});

test("a sent prompt shows until the journal holds it, one message for one prompt", () => {
  const at = 1_000_000;
  const prompt = (key: string, text: string, after = 10): PendingPrompt => ({ key, text, mode: "steer", sentAt: at, after });
  const message = (seq: number, text: string, kind = "user.message"): TimelineEvent => ({ seq, at: null, kind, text, activityId: null });
  const pending = [prompt("p1", "Use the fast path"), prompt("p2", "Use the fast path")];
  assert.deepEqual(stillPending(pending, [message(5, "Use the fast path")], true, at).map(p => p.key), ["p1", "p2"], "a message from before the send is not it");
  assert.deepEqual(stillPending(pending, [message(11, " Use the fast path ")], true, at).map(p => p.key), ["p2"]);
  assert.deepEqual(stillPending(pending, [message(11, "Use the fast path", "assistant.message")], true, at).map(p => p.key), ["p1", "p2"]);
  // Idle, a prompt the journal never wrote goes after a while.
  assert.deepEqual(stillPending(pending, [], false, at + pendingGrace - 1).length, 2);
  assert.deepEqual(stillPending(pending, [], false, at + pendingGrace).length, 0);
  assert.deepEqual(stillPending(pending, [], true, at + 10 * pendingGrace).length, 2, "kept while the turn runs");
});
