import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse } from "#neoastra";
import { loadHistory, historyMessage, historySettled, mergeHistoryPage, type HistoryState } from "./history";

const page: HistoryResponse = { status: "ok", entries: [], next: null, tailOmitted: false };
const request = { sessionId: "s", cursor: null };
const signal = { aborted: false } as AbortSignal;

test("reverse pages retain the latest user prompt across the 1,000-event window and preserve an older cursor", () => {
  const entries = Array.from({ length: 1205 }, (_, index) => ({
    offset: `${index * 200}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: index === 1204 ? "User" : "Assistant", phase: null,
    contentId: `${index}`, activityId: null, parentActivityId: null, interactionId: null, name: null,
    text: index === 1204 ? "latest user prompt" : `${index}`, details: null, textTruncated: false,
    files: null, detailsTruncated: false, bodyOmitted: false,
  } satisfies HistoryResponse["entries"][number]));
  let timeline;
  for (let end = 1205; end > 0 && (timeline?.entries.length ?? 0) < 1000; end -= 100) {
    const start = Math.max(0, end - 100);
    const older = start ? { version: 2, sessionId: "s", length: "300000", lastWriteUtcTicks: "7", offset: entries[start].offset } : null;
    timeline = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline?.next ?? null },
      { ...page, entries: entries.slice(start, end), next: older });
  }
  assert.equal(timeline?.entries.length, 1000);
  assert.equal(timeline?.entries.at(-1)?.text, "latest user prompt");
  assert.equal(timeline?.entries[0].contentId, "205");
  assert.equal(timeline?.next?.version, 2);
  assert.equal(timeline?.next?.offset, entries[205].offset);
  assert.equal(historySettled({ kind: "ready", request, page }, timeline), true, "stop automatic reads at the cap, not at journal start");
});

test("an uneven initial page keeps the newest 1,000 and rewinds the older cursor to the retained boundary", () => {
  const entry = (index: number): HistoryResponse["entries"][number] => ({
    offset: `${index * 200}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: `${index}`,
    activityId: null, parentActivityId: null, interactionId: null, name: null, text: `turn-${index}`, details: null,
    files: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  });
  let timeline;
  for (let end = 1205; end > 0 && (timeline?.entries.length ?? 0) < 1000;) {
    const start = Math.max(0, end - (end === 1205 ? 75 : 100));
    timeline = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline?.next ?? null },
      { ...page, entries: Array.from({ length: end - start }, (_, i) => entry(start + i)),
        next: start ? { version: 2, sessionId: "s", length: "300000", lastWriteUtcTicks: "7", offset: `${start * 200}` } : null });
    end = start;
  }
  assert.equal(timeline?.entries[0].contentId, "205");
  assert.equal(timeline?.entries.at(-1)?.contentId, "1204");
  assert.equal(timeline?.next?.offset, entry(205).offset, "older paging must not skip rows discarded at the cap");
  const older = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline?.next ?? null },
    { ...page, entries: Array.from({ length: 100 }, (_, i) => entry(i + 105)),
      next: { version: 2, sessionId: "s", length: "300000", lastWriteUtcTicks: "7", offset: entry(105).offset } }, true);
  assert.equal(older.entries[0].contentId, "105");
  assert.equal(older.entries.at(-1)?.contentId, "1104");
  assert.equal(older.newerOmitted, true);
  assert.equal(mergeHistoryPage(older, { sessionId: "s", cursor: { ...older.next!, version: 1 } },
    { ...page, entries: [entry(1)] }).entries.length, 1, "forward cursors cannot merge into the reverse window");
  assert.equal(mergeHistoryPage(older, { sessionId: "other", cursor: older.next },
    { ...page, entries: [entry(1)] }).sessionId, "other");
});

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
    files: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  };
  const cursor = { version: 2, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" };
  const first = mergeHistoryPage(undefined, request, { ...page, entries: [{ ...entry, offset: "10" }], next: cursor });
  const next = { ...request, cursor: { ...cursor } };
  const replacement = { ...page, entries: [{ ...entry, eventType: "contentCompleted", text: "older" }] };
  const accumulated = mergeHistoryPage(first, next, replacement);
  assert.deepEqual(accumulated.entries.map(value => value.text), ["older", "delta"]);
  assert.equal(mergeHistoryPage(accumulated, request, { ...page, entries: [{ ...entry, text: "fresh" }] }).entries[0].text, "fresh");
});

test("timeline paging de-duplicates offsets and remains bounded", () => {
  const entries = Array.from({ length: 1000 }, (_, index) => ({
    offset: `${index + 100}`, eventType: "contentCompleted", providerId: "p", sessionId: "s", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: `${index}`,
    activityId: null, parentActivityId: null, interactionId: null, name: null, text: `${index}`, details: null,
    files: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  }));
  const cursor = { version: 2, sessionId: "s", length: "2000", lastWriteUtcTicks: "7", offset: "1000" };
  const first = mergeHistoryPage(undefined, request, { ...page, entries, next: cursor });
  assert.equal(first.entries.length, 1000);
  assert.equal(first.limitReached, true);
  assert.deepEqual(first.next, cursor);
  const duplicate = mergeHistoryPage(first, { ...request, cursor }, { ...page, entries: [entries[999]] }, true);
  assert.equal(duplicate.entries.length, 1000);
  assert.equal(duplicate.entries.at(-1)?.offset, "1099");
  const older = mergeHistoryPage(first, { ...request, cursor }, { ...page, entries: [
    { ...entries[0], offset: "98" }, { ...entries[0], offset: "99" }] }, true);
  assert.equal(older.entries.at(-1)?.offset, "1097");
  assert.equal(older.newerOmitted, true);
  assert.equal(mergeHistoryPage(older, request, { ...page, entries: [entries[999]] }).newerOmitted, false);
});

test("history change resets the cursor", async () => {
  const states: HistoryState[] = [];
  const old = { ...request, cursor: { version: 2, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" } };
  await loadHistory(async () => ({ ...page, status: "history_changed" }), old, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "error", request: old, code: "history_changed" });
  assert.match(historyMessage("history_changed"), /refresh/i);
  // Explicit restart, not an automatic retry loop; the actual button is source-guarded.
  await loadHistory(async (actual) => { assert.equal(actual.cursor, null); return page; }, request, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "ready", request, page });
});

test("empty pages preserve continuation", async () => {
  const next = { version: 2, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" };
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
  const next = { version: 2, sessionId: "s", length: "100", lastWriteUtcTicks: "7", offset: "10" };
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
