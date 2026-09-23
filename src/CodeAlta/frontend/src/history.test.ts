import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse } from "#neoastra";
import { loadHistory, historyMessage, historySettled, mergeHistoryPage, type HistoryState } from "./history";

const page: HistoryResponse = { status: "ok", entries: [], next: null, tailOmitted: false };
const request = { sessionId: "s", cursor: null };
const signal = { aborted: false } as AbortSignal;

test("selection loads persisted history", async () => {
  const states: HistoryState[] = [];
  await loadHistory(async (actual, options) => {
    assert.deepEqual(actual, request);
    assert.equal(options.signal, signal);
    return page;
  }, request, signal, state => states.push(state));
  assert.deepEqual(states.map(state => state.kind), ["loading", "ready"]);
});

test("selection change suppresses stale success", async () => {
  const states: HistoryState[] = [];
  let aborted = false;
  const controlled = { get aborted() { return aborted; } } as AbortSignal;
  const original = loadHistory(async () => page, request, controlled, state => states.push(state));
  aborted = true;
  await original;
  assert.equal(states.length, 1);
});

test("selection change suppresses stale failure", async () => {
  const states: HistoryState[] = [];
  let aborted = false;
  const controlled = { get aborted() { return aborted; } } as AbortSignal;
  const original = loadHistory(async () => { throw new Error("late private failure"); }, request, controlled, state => states.push(state));
  aborted = true;
  await original;
  assert.equal(states.length, 1);
});

test("reselection does not revive an old request", async () => {
  const states: HistoryState[] = [];
  let aborted = false;
  const oldSignal = { get aborted() { return aborted; } } as AbortSignal;
  const original = loadHistory(async () => ({ ...page, status: "missing_session" }), request, oldSignal, state => states.push(state));
  aborted = true;
  await loadHistory(async () => page, request, signal, state => states.push(state));
  await original;
  assert.equal(states.at(-1)?.kind, "ready");
  await loadHistory(async () => { assert.fail("aborted request started"); }, request, oldSignal, state => states.push(state));
  assert.equal(states.length, 3);
});

test("timeline paging accumulates distinct rows and an explicit restart replaces them", () => {
  const entry: HistoryResponse["entries"][number] = {
    offset: "0", eventType: "contentDelta", providerId: "p", sessionId: "runtime", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: "content",
    activityId: null, parentActivityId: null, interactionId: null, name: null, text: "delta", details: null,
    textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  };
  const cursor = { version: 1, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" };
  const first = mergeHistoryPage(undefined, request, { ...page, entries: [entry], next: cursor });
  const next = { ...request, cursor: { version: 1, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" } };
  const replacement = { ...page, entries: [{ ...entry, offset: "10", eventType: "contentCompleted", text: "final" }] };
  const accumulated = mergeHistoryPage(first, next, replacement);
  assert.deepEqual(accumulated.entries.map(value => value.text), ["delta", "final"]);
  assert.equal(mergeHistoryPage(accumulated, request, { ...page, entries: [{ ...entry, text: "fresh" }] }).entries[0].text, "fresh");
});

test("timeline paging de-duplicates offsets and remains bounded", () => {
  const entries = Array.from({ length: 1000 }, (_, index) => ({
    offset: `${index}`, eventType: "contentCompleted", providerId: "p", sessionId: "s", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: `${index}`,
    activityId: null, parentActivityId: null, interactionId: null, name: null, text: `${index}`, details: null,
    textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  }));
  const cursor = { version: 1, sessionId: "s", length: "2000", lastWriteUtcTicks: "7", offset: "1000" };
  const first = mergeHistoryPage(undefined, request, { ...page, entries, next: cursor });
  assert.equal(first.entries.length, 1000);
  assert.equal(first.limitReached, true);
  assert.equal(first.next, null);
  const duplicate = mergeHistoryPage(first, { ...request, cursor }, { ...page, entries: [entries[999]] });
  assert.equal(duplicate.entries.length, 1000);
});

test("history change resets the cursor", async () => {
  const states: HistoryState[] = [];
  const old = { ...request, cursor: { version: 1, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" } };
  await loadHistory(async () => ({ ...page, status: "history_changed" }), old, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "error", request: old, code: "history_changed" });
  assert.match(historyMessage("history_changed"), /restart/i);
  // Explicit restart, not an automatic retry loop; the actual button is source-guarded.
  await loadHistory(async (actual) => { assert.equal(actual.cursor, null); return page; }, request, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "ready", request, page });
});

test("empty pages preserve continuation", async () => {
  const next = { version: 1, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" };
  const states: HistoryState[] = [];
  await loadHistory(async () => ({ ...page, next }), request, signal, state => states.push(state));
  const last = states.at(-1);
  assert.equal(last?.kind, "ready");
  if (last?.kind === "ready") assert.deepEqual(last.page.next, next);
});

test("history displays structured failures and omission notices", async () => {
  const states: HistoryState[] = [];
  await loadHistory(async () => { throw new Error("private/cache"); }, request, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "error", request, code: "read_failed" });
  assert.doesNotMatch(historyMessage("read_failed"), /private/);
  assert.match(historyMessage("unsupported_format"), /UTF-8/);
  await loadHistory(async () => ({ ...page, tailOmitted: true }), request, signal, state => states.push(state));
  const last = states.at(-1);
  if (last?.kind !== "ready") assert.fail("missing page");
  assert.equal(last.page.tailOmitted, true);
});

test("reading position waits for final page or explicit error, never loading or an obsolete session page", async () => {
  const states: HistoryState[] = [];
  assert.equal(historySettled(undefined, undefined), false);
  const next = { version: 1, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" };
  await loadHistory(async () => ({ ...page, next }), request, signal, state => states.push(state));
  const ready = states.at(-1);
  assert.equal(historySettled(ready, undefined), false);
  if (ready?.kind !== "ready") assert.fail("missing page");
  const first = mergeHistoryPage(undefined, request, ready.page);
  assert.equal(historySettled(ready, first), false);
  assert.equal(historySettled(ready, { ...first, next: null, sessionId: "other" }), false);
  const secondRequest = { ...request, cursor: next };
  await loadHistory(async () => page, secondRequest, signal, state => states.push(state));
  const last = states.at(-1);
  if (last?.kind !== "ready") assert.fail("missing last page");
  assert.equal(historySettled(last, mergeHistoryPage(first, secondRequest, last.page)), true);
  await loadHistory(async () => { throw new Error("private failure"); }, request, signal, state => states.push(state));
  assert.equal(historySettled(states.at(-1), undefined), true);
});
