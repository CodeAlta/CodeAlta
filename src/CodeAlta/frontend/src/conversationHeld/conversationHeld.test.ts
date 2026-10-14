import assert from "node:assert/strict";
import test from "node:test";
import type { ConversationHeldEntry, ConversationHeldPage, ConversationHeldResolveRequest, ConversationHeldResult } from "#neoastra";
import { createConversationHeldReviewer, holderDetail, offered, originKey, originKeyFromText, type HeldState } from "./conversationHeld";

const epoch = "11111111-1111-4111-8111-111111111111";
const entry = (attempt = "22222222-2222-4222-8222-222222222222", choices = ["wait", "continueAsCopy", "openAnyway", "cancel"]): ConversationHeldEntry => ({
  handle: { runtimeInstanceId: "33333333-3333-4333-8333-333333333333", attachmentGeneration: "1", sessionId: "session", runId: "run",
    requestId: "request", attemptId: attempt },
  providerId: "claude-code", timestamp: "2026-10-14T00:00:00Z", conversationId: "conversation",
  holder: { origin: "vsCode", originText: "VS Code", processId: 4242, entrypoint: "claude-vscode", kind: null, status: null,
    workingDirectory: "C:\\work", name: null },
  choices,
});
const page = (entries: ConversationHeldEntry[], status = "ok", hostEpoch = epoch): ConversationHeldPage => ({ status, hostEpoch, sessionId: "session", entries });

function fixture(pages: (ConversationHeldPage | Error)[], results: (ConversationHeldResult | Error)[] = []) {
  const states: HeldState[] = [];
  const answers: ConversationHeldResolveRequest[] = [];
  let lists = 0;
  const reviewer = createConversationHeldReviewer(async () => {
    const next = pages[Math.min(lists++, pages.length - 1)];
    if (next instanceof Error) throw next;
    return next;
  }, async request => {
    answers.push(request);
    const next = results.shift();
    if (!next || next instanceof Error) throw next ?? Error("no reply");
    return next;
  });
  const controller = new AbortController();
  const scope = reviewer.forSelection(epoch, "session", controller.signal, state => states.push(state));
  return { scope, states, answers, lists: () => lists, controller, reviewer };
}
const last = (states: HeldState[]) => states[states.length - 1];

test("a question is listed, answered once, and the list is read again", async () => {
  const shown = entry();
  const f = fixture([page([shown]), page([])], [{ status: "resolved", hostEpoch: epoch, handle: shown.handle }]);
  await f.scope.refresh();
  const ready = last(f.states);
  assert.equal(ready.kind, "ready");
  const listed = (ready as Extract<HeldState, { kind: "ready" }>).entries[0];
  await f.scope.answer(listed, "wait");
  assert.deepEqual(f.answers.map(a => [a.choice, a.handle.attemptId, a.expectedHostEpoch]), [["wait", shown.handle.attemptId, epoch]]);
  assert.deepEqual(f.states.map(s => s.kind), ["loading", "ready", "resolving", "loading", "ready"]);
  assert.deepEqual(last(f.states), { kind: "ready", entries: [], notice: null });
  // The question is no longer shown: answering it again sends nothing.
  await f.scope.answer(listed, "wait");
  assert.equal(f.answers.length, 1);
});

test("an answer that is rejected or uncertain is never sent again: the list is read again with a notice", async () => {
  for (const [reply, notice] of [[{ status: "rejected" }, "rejected"], [{ status: "uncertain" }, "uncertain"], [Error("transport"), "uncertain"],
    [{ status: "resolved", handle: null }, "uncertain"]] as const) {
    const shown = entry();
    const result = reply instanceof Error ? reply : { hostEpoch: epoch, handle: shown.handle, ...reply } as ConversationHeldResult;
    const f = fixture([page([shown]), page([shown])], [result]);
    await f.scope.refresh();
    const listed = (last(f.states) as Extract<HeldState, { kind: "ready" }>).entries[0];
    await f.scope.answer(listed, "openAnyway");
    assert.equal(f.answers.length, 1);
    assert.equal(f.lists(), 2);
    const after = last(f.states) as Extract<HeldState, { kind: "ready" }>;
    assert.equal(after.kind, "ready");
    assert.equal(after.notice, notice);
    assert.equal(after.entries.length, 1, "What still waits is shown again, to be answered anew");
  }
});

test("a host epoch that changed asks for a reload, for every later selection", async () => {
  const shown = entry();
  const f = fixture([page([shown]), page([])], [{ status: "stale_epoch", hostEpoch: "44444444-4444-4444-8444-444444444444", handle: shown.handle }]);
  await f.scope.refresh();
  await f.scope.answer((last(f.states) as Extract<HeldState, { kind: "ready" }>).entries[0], "continueAsCopy");
  assert.deepEqual(last(f.states), { kind: "error", code: "stale_epoch", reloadRequired: true });
  assert.equal(f.lists(), 1, "Nothing is read once the epoch is stale");
  const states: HeldState[] = [];
  const other = f.reviewer.forSelection(epoch, "session", new AbortController().signal, state => states.push(state));
  await other.refresh();
  assert.deepEqual(states, [{ kind: "error", code: "stale_epoch", reloadRequired: true }]);
});

test("a page of another epoch or session, or with a malformed question, is refused", async () => {
  for (const [value, code, reload] of [[page([entry()], "ok", "44444444-4444-4444-8444-444444444444"), "stale_epoch", true],
    [{ ...page([entry()]), sessionId: "other" }, "invalid_response", false],
    [page([{ ...entry(), handle: { ...entry().handle, attemptId: "not-a-guid" } }]), "invalid_response", false],
    [page([entry(undefined, ["later"])]), "invalid_response", false],
    [page([], "read_failed"), "read_failed", false], [Error("down"), "read_failed", false]] as const) {
    const f = fixture([value]);
    await f.scope.refresh();
    assert.deepEqual(last(f.states), { kind: "error", code, reloadRequired: reload }, JSON.stringify(value));
  }
});

test("a choice the question did not offer is not sent", async () => {
  const shown = entry(undefined, ["wait", "continueAsCopy", "cancel"]);
  const f = fixture([page([shown])]);
  await f.scope.refresh();
  await f.scope.answer((last(f.states) as Extract<HeldState, { kind: "ready" }>).entries[0], "openAnyway");
  assert.equal(f.answers.length, 0);
});

test("a selection that ends publishes nothing more", async () => {
  const f = fixture([page([entry()])]);
  f.controller.abort();
  await f.scope.refresh();
  assert.deepEqual(f.states, []);
});

test("the card offers the known choices in their order, Cancel last, and names where the conversation is open", () => {
  assert.deepEqual(offered(entry(undefined, ["cancel", "wait", "unknown", "openAnyway"])), ["wait", "openAnyway", "cancel"]);
  assert.deepEqual(offered(entry(undefined, ["wait", "continueAsCopy"])), ["wait", "continueAsCopy"]);
  assert.equal(originKey("vsCode", "VS Code"), "VS Code");
  assert.equal(originKey("terminal", "a terminal"), "a terminal");
  assert.equal(originKey("backgroundSession", "a background session of Claude Code"), "a background session of Claude Code");
  assert.equal(originKey("otherApplication", "another application"), "another application");
  assert.equal(originKey("otherApplication", "another application (sdk-ts)"), null, "Named by how it was started: the host's words");
  assert.equal(originKey("unknown", "another Claude process"), "another Claude process");
  assert.equal(originKeyFromText("a terminal"), "a terminal");
  assert.equal(originKeyFromText("somewhere"), null);
  assert.deepEqual(holderDetail(entry()), { processId: 4242, workingDirectory: "C:\\work" });
  assert.deepEqual(holderDetail({ ...entry(), holder: { ...entry().holder, processId: null, workingDirectory: "  " } }), { processId: null, workingDirectory: null });
});
