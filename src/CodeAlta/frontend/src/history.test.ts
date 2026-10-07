import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryRequest, HistoryResponse } from "#neoastra";
import { loadHistory, historyCanRetry, historyMessage, historySettled, maximumTimelineEntries, mergeHistoryPage, type HistoryState, type HistoryTimeline } from "./history";

const page: HistoryResponse = { status: "ok", entries: [], next: null, tailOmitted: false };
const request = { sessionId: "s", cursor: null };
const signal = { aborted: false } as AbortSignal;

test("failed history page request is reported to the console by code only", async t => {
  const warn = t.mock.method(console, "warn", () => {});
  const states: HistoryState[] = [];
  await loadHistory(async () => { throw Object.assign(new Error("private transport failure"), { code: "timeout" }); }, request, signal, state => states.push(state));
  assert.deepEqual(states.at(-1), { kind: "error", request, code: "read_failed" });
  assert.equal(warn.mock.callCount(), 1);
  assert.deepEqual(warn.mock.calls[0].arguments, ["[CodeAlta History] page request failed", { code: "timeout" }]);
  assert.equal(JSON.stringify(warn.mock.calls[0].arguments).includes("private"), false);
});

test("transient history read failure remains eligible for bounded live-refresh recovery", async () => {
  const states: HistoryState[] = [];
  await loadHistory(async () => { throw new Error("private transport failure"); }, request, signal, state => states.push(state));
  assert.equal(historyCanRetry(states.at(-1)), true);
  assert.equal(historyCanRetry({ kind: "error", request, code: "history_changed" }), true);
  await loadHistory(async () => page, request, signal, state => states.push(state));
  assert.equal(historyCanRetry(states.at(-1)), false);
  assert.equal(historyCanRetry({ kind: "loading", request }), false);
  assert.equal(historyCanRetry(undefined), false);
  for (const code of ["missing_session", "outside_root", "invalid_cursor", "corrupt_record", "wire_limit", "timeline_record_too_large"])
    assert.equal(historyCanRetry({ kind: "error", request, code }), false);
});

test("reverse pages stop at the latest user prompt, or at the 1,000-event cap, and preserve an older cursor", () => {
  const journal = (user: number) => Array.from({ length: 1205 }, (_, index) => ({
    offset: `${index * 200}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: index === user ? "User" : "Assistant", phase: null,
    contentId: `${index}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: index === user ? "latest user prompt" : `${index}`, details: null, textTruncated: false,
    tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false,
  } satisfies HistoryResponse["entries"][number]));
  const load = (entries: ReturnType<typeof journal>) => {
    let timeline: HistoryTimeline | undefined;
    for (let end = 1205; end > 0 && !historySettled({ kind: "ready", request, page }, timeline); end -= 100) {
      const start = Math.max(0, end - 100);
      const older = start ? { version: 2, sessionId: "s", length: "300000", lastWriteUtcTicks: "7", offset: entries[start].offset } : null;
      timeline = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline?.next ?? null },
        { ...page, entries: entries.slice(start, end), next: older });
    }
    return timeline!;
  };
  // The latest turn spans several pages: the window starts at its user prompt and keeps the tail.
  let entries = journal(850);
  let timeline = load(entries);
  assert.equal(timeline.turnReached, true);
  assert.equal(timeline.entries.length, 355);
  assert.equal(timeline.entries[0].text, "latest user prompt");
  assert.equal(timeline.entries.at(-1)?.contentId, "1204");
  assert.equal(timeline.next?.version, 2);
  assert.equal(timeline.next?.offset, entries[850].offset, "older paging continues right before the prompt");
  // A turn longer than the window stops at the cap, not at journal start, and keeps the newest events.
  entries = journal(100);
  timeline = load(entries);
  assert.equal(timeline.turnReached, false);
  assert.equal(timeline.entries.length, 1000);
  assert.equal(timeline.entries[0].contentId, "205");
  assert.equal(timeline.entries.at(-1)?.contentId, "1204");
  assert.equal(timeline.next?.offset, entries[205].offset);
  assert.equal(timeline.limitReached, true);
});

test("an uneven initial page keeps the newest 1,000 and rewinds the older cursor to the retained boundary", () => {
  const entry = (index: number): HistoryResponse["entries"][number] => ({
    offset: `${index * 200}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: `${index}`,
    activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null, text: `turn-${index}`, details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
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
  // Older history extends the window: nothing newer is dropped to make room for it.
  assert.equal(older.entries[0].contentId, "105");
  assert.equal(older.entries.at(-1)?.contentId, "1204");
  assert.equal(older.entries.length, 1100);
  assert.equal(older.newerOmitted, false);
  assert.equal(mergeHistoryPage(older, { sessionId: "s", cursor: { ...older.next!, version: 1 } },
    { ...page, entries: [entry(1)] }).entries.length, 1, "forward cursors cannot merge into the reverse window");
  assert.equal(mergeHistoryPage(older, { sessionId: "other", cursor: older.next },
    { ...page, entries: [entry(1)] }).sessionId, "other");
});

test("replaying an already merged page keeps the loaded window", () => {
  const entry = (index: number): HistoryResponse["entries"][number] => ({
    offset: `${index * 200}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: index === 120 ? "User" : "Assistant", phase: null, contentId: `${index}`,
    activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null, text: `turn-${index}`, details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  });
  const revision = { sessionId: "s", length: "70000", lastWriteUtcTicks: "7" };
  const read = (cursor: HistoryRequest["cursor"]) => {
    const end = cursor ? Number(cursor.offset) / 200 : 350;
    const start = Math.max(0, end - 100);
    return { ...page, revision, entries: Array.from({ length: end - start }, (_, i) => entry(start + i)),
      next: start ? { version: 2, ...revision, offset: `${start * 200}` } : null };
  };
  // The latest turn starts three reverse pages back, at the user prompt in the last page read.
  const requests: HistoryRequest[] = [];
  let timeline: HistoryTimeline | undefined;
  while (!timeline?.turnReached) {
    requests.push({ sessionId: "s", cursor: timeline?.next ?? null });
    timeline = mergeHistoryPage(timeline, requests.at(-1)!, read(requests.at(-1)!.cursor));
  }
  assert.deepEqual(requests.map(value => value.cursor?.offset ?? "end"), ["end", "50000", "30000"]);
  assert.equal(timeline.entries.length, 230);
  // A pane that becomes visible again can ask for a page it already merged: neither the page
  // holding the turn boundary nor an intermediate page may replace the newer loaded rows.
  for (const replayed of [requests[2], requests[1]]) {
    const replay = mergeHistoryPage(timeline, replayed, read(replayed.cursor), false, timeline.entries[0].offset);
    assert.equal(replay.entries.length, 230);
    assert.equal(replay.entries[0].text, "turn-120");
    assert.equal(replay.entries.at(-1)?.text, "turn-349");
    assert.deepEqual(replay.next, timeline.next);
  }
  // The same holds after an explicit older page, and once the whole journal is loaded (no older cursor).
  let older = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline.next }, read(timeline.next), true);
  assert.equal(older.entries.length, 330);
  assert.equal(mergeHistoryPage(older, requests[2], read(requests[2].cursor), true).entries.length, 330);
  const last = { sessionId: "s", cursor: older.next };
  older = mergeHistoryPage(older, last, read(last.cursor), true);
  assert.equal(older.next, null);
  assert.equal(older.entries.length, 350);
  assert.equal(mergeHistoryPage(older, last, read(last.cursor), true).entries.length, 350);
  // Another journal revision is never a replay: it starts afresh.
  const changed = { sessionId: "s", cursor: { ...requests[2].cursor!, lastWriteUtcTicks: "8" } };
  assert.equal(mergeHistoryPage(timeline, changed, read(changed.cursor), true).entries.length, 100);
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
    activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null, text: "delta", details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
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
    activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null, text: `${index}`, details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
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
  assert.equal(older.entries.length, 1002);
  assert.equal(older.entries.at(-1)?.offset, "1099");
  assert.equal(older.newerOmitted, false);
});

test("a window that reaches its largest size slides instead of growing", () => {
  const entry = (index: number) => ({
    offset: `${index + 100}`, eventType: "contentCompleted", providerId: "p", sessionId: "s", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: `${index}`,
    activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null, text: "x", details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false,
  });
  const cursor = { version: 2, sessionId: "s", length: "900000", lastWriteUtcTicks: "7", offset: "100" };
  const full: HistoryTimeline = { sessionId: "s", entries: Array.from({ length: maximumTimelineEntries }, (_, index) => entry(index)), next: cursor,
    tailOmitted: false, limitReached: false, newerOmitted: false, pages: 1 };
  const older = mergeHistoryPage(full, { ...request, cursor }, { ...page, entries: [{ ...entry(0), offset: "98" }, { ...entry(0), offset: "99" }],
    next: { ...cursor, offset: "98" } }, true, undefined, { olderFrom: "100", all: true });
  assert.equal(older.entries.length, maximumTimelineEntries);
  assert.equal(older.entries[0].offset, "98");
  assert.equal(older.entries.at(-1)?.offset, `${maximumTimelineEntries + 97}`);
  assert.equal(older.newerOmitted, true, "the newest records left the window");
  assert.equal(older.limitReached, true);
  assert.equal(mergeHistoryPage(older, request, { ...page, entries: [entry(5)] }).newerOmitted, false);
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

test("a refresh of the newest history keeps the loaded window and appends what is new", () => {
  const row = (offset: number, kind = "Assistant") => ({ offset: `${offset}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: "r",
    timestamp: "2026-01-01T00:00:00Z", kind, phase: null, contentId: `${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: `${offset}`, details: null, textTruncated: false, tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false } satisfies HistoryResponse["entries"][number]);
  const cursor = (length: number, offset: number) => ({ version: 2, sessionId: "s", length: `${length}`, lastWriteUtcTicks: `${length}`, offset: `${offset}` });
  // Two turns are loaded (the older one on request); the journal then grows by one answer and a new turn.
  const known: HistoryTimeline = { sessionId: "s", entries: [row(100, "User"), row(200), row(300, "User"), row(400)], next: cursor(500, 100),
    tailOmitted: false, limitReached: false, newerOmitted: false, turnReached: true, pages: 2,
    revision: { sessionId: "s", length: "500", lastWriteUtcTicks: "500" }, sources: [{ start: "100", end: "200" }] };
  const tail = { ...page, entries: [row(400), row(500), row(600, "User"), row(700)], next: cursor(800, 400),
    revision: { sessionId: "s", length: "800", lastWriteUtcTicks: "800" }, sources: [{ start: "700", end: "800" }] };
  const refreshed = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, tail, false, "100", { known });
  assert.deepEqual(refreshed.entries.map(entry => entry.offset), ["100", "200", "300", "400", "500", "600", "700"]);
  assert.equal(refreshed.turnReached, true);
  assert.equal(historySettled({ kind: "ready", request: { sessionId: "s", cursor: null }, page: tail }, refreshed), true);
  assert.deepEqual(refreshed.next, cursor(800, 100), "the older cursor keeps its place in the new journal revision");
  assert.deepEqual(refreshed.sources, [{ start: "700", end: "800" }, { start: "100", end: "200" }]);

  // More was appended than one page holds: the read continues until it meets the window.
  const far = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, { ...tail, entries: [row(900), row(1000)], next: cursor(1100, 900) }, false, "100", { known });
  assert.equal(far.turnReached, false);
  assert.deepEqual(far.entries.map(entry => entry.offset), ["900", "1000"]);
  const met = mergeHistoryPage(far, { sessionId: "s", cursor: far.next }, { ...tail, entries: [row(400), row(500)], next: cursor(1100, 400) }, false, "100", { known });
  assert.deepEqual(met.entries.map(entry => entry.offset), ["100", "200", "300", "400", "500", "900", "1000"]);
  assert.equal(met.turnReached, true);

  // A window that slid away from the journal end, or another session's, is not kept.
  for (const other of [{ ...known, newerOmitted: true }, { ...known, sessionId: "other" }]) {
    const fresh = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, tail, false, undefined, { known: other });
    assert.deepEqual(fresh.entries.map(entry => entry.offset), ["600", "700"]);
  }
  assert.equal(mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, { ...tail, next: null }, false, undefined, { known: { ...known, next: null } }).next, null);
});

test("a refresh keeps a window that older history made larger than a first read", () => {
  const row = (offset: number, kind = "Assistant") => ({ offset: `${offset}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: "r",
    timestamp: "2026-01-01T00:00:00Z", kind, phase: null, contentId: `${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: `${offset}`, details: null, textTruncated: false, tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false } satisfies HistoryResponse["entries"][number]);
  // The whole journal was loaded: 5,000 records, far more than the 1,000 a first read keeps.
  const known: HistoryTimeline = { sessionId: "s", entries: Array.from({ length: 5000 }, (_, index) => row(index * 10, index % 10 === 0 ? "User" : "Assistant")), next: null,
    tailOmitted: false, limitReached: false, newerOmitted: false, turnReached: false, pages: 50 };
  const tail = { ...page, entries: [row(49_980), row(49_990), row(50_000)], next: { version: 2, sessionId: "s", length: "60000", lastWriteUtcTicks: "9", offset: "49980" } };
  const refreshed = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, tail, false, "0", { known });
  assert.equal(refreshed.entries.length, 5001, "the loaded history stays and the new record joins it");
  assert.equal(refreshed.entries[0], known.entries[0], "the records of the window are the same objects, so their rows are not rebuilt");
  assert.equal(refreshed.entries.at(-1)?.offset, "50000");
  assert.equal(refreshed.next, null);
  assert.equal(refreshed.newerOmitted, false);
  assert.equal(historySettled({ kind: "ready", request: { sessionId: "s", cursor: null }, page }, refreshed), true);
});

test("older history is read in chunks of whole turns", () => {
  const row = (offset: number, kind = "Assistant") => ({ offset: `${offset}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: "r",
    timestamp: "2026-01-01T00:00:00Z", kind, phase: null, contentId: `${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: `${offset}`, details: null, textTruncated: false, tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false } satisfies HistoryResponse["entries"][number]);
  const cursor = (offset: number) => ({ version: 2, sessionId: "s", length: "900", lastWriteUtcTicks: "9", offset: `${offset}` });
  const window: HistoryTimeline = { sessionId: "s", entries: [row(700, "User"), row(800)], next: cursor(700),
    tailOmitted: false, limitReached: false, newerOmitted: false, turnReached: true, pages: 31 };
  const options = { olderFrom: "700" };
  // Turns of ten records: a prompt at every offset that ends in 0000.
  const turn = (from: number, to: number) => Array.from({ length: (to - from) / 1000 }, (_, index) => {
    const offset = from + index * 1000;
    return row(offset, offset % 10_000 === 0 ? "User" : "Assistant");
  });
  const big: HistoryTimeline = { ...window, entries: [row(700_000, "User"), row(701_000)], next: cursor(700_000) };
  const from = { olderFrom: "700000" };
  // A page that holds whole turns but too few records: the read is not settled and goes on.
  const first = mergeHistoryPage(big, { sessionId: "s", cursor: big.next }, { ...page, entries: turn(600_000, 700_000), next: cursor(600_000) }, true, undefined, from);
  assert.equal(first.entries.length, 102);
  assert.equal(first.turnReached, false);
  assert.equal(first.limitReached, false, "each older read has its own page budget");
  assert.equal(historySettled({ kind: "ready", request: { sessionId: "s", cursor: big.next }, page }, first), false);
  // Enough records now: the window starts at the oldest turn read completely. The turn at the very start of
  // what was read may continue in the page before it (its preparation records), so the next one is taken.
  const second = mergeHistoryPage(first, { sessionId: "s", cursor: first.next }, { ...page, entries: turn(395_000, 600_000), next: cursor(395_000) }, true, undefined, from);
  assert.equal(second.turnReached, true);
  assert.equal(second.entries[0].offset, "400000");
  assert.equal(second.entries.length, 302);
  assert.deepEqual(second.next, cursor(400_000));
  assert.equal(second.newerOmitted, false);
  assert.equal(historySettled({ kind: "ready", request: { sessionId: "s", cursor: first.next }, page }, second), true);
  const exact = mergeHistoryPage(first, { sessionId: "s", cursor: first.next }, { ...page, entries: turn(400_000, 600_000), next: cursor(400_000) }, true, undefined, from);
  assert.equal(exact.entries[0].offset, "410000", "a turn that starts with the first record read is not cut at yet");
  // The journal start ends the read, with or without a prompt, however few records it brought.
  const start = mergeHistoryPage(window, { sessionId: "s", cursor: window.next }, { ...page, entries: [row(600)], next: null }, true, undefined, options);
  assert.equal(start.next, null);
  assert.deepEqual(start.entries.map(entry => entry.offset), ["600", "700", "800"]);
  assert.equal(historySettled({ kind: "ready", request: { sessionId: "s", cursor: window.next }, page }, start), true);
});

test("loading all older history reads to the start of the journal without cutting at turns", () => {
  const row = (offset: number, kind = "Assistant") => ({ offset: `${offset}`, eventType: "contentCompleted", providerId: "p", sessionId: "runtime", runId: "r",
    timestamp: "2026-01-01T00:00:00Z", kind, phase: null, contentId: `${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: `${offset}`, details: null, textTruncated: false, tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false } satisfies HistoryResponse["entries"][number]);
  const cursor = (offset: number) => ({ version: 2, sessionId: "s", length: "900000", lastWriteUtcTicks: "9", offset: `${offset}` });
  let timeline: HistoryTimeline = { sessionId: "s", entries: [row(500_000, "User"), row(500_001)], next: cursor(500_000),
    tailOmitted: false, limitReached: false, newerOmitted: false, turnReached: true, pages: 1 };
  const options = { olderFrom: "500000", all: true };
  // Fifty pages of a hundred records, a prompt every ten: far past one chunk and its page budget.
  for (let end = 500_000; end > 0; end -= 10_000) {
    const start = end - 10_000;
    const request = { sessionId: "s", cursor: timeline.next };
    timeline = mergeHistoryPage(timeline, request, { ...page, entries: Array.from({ length: 100 }, (_, index) => row(start + index * 100, index % 10 === 0 ? "User" : "Assistant")),
      next: start ? cursor(start) : null }, true, undefined, options);
    assert.equal(timeline.turnReached, false);
    assert.equal(timeline.limitReached, false);
    assert.equal(historySettled({ kind: "ready", request, page }, timeline), start === 0);
  }
  assert.equal(timeline.entries.length, 5002);
  assert.equal(timeline.entries[0].offset, "0");
  assert.equal(timeline.next, null);
  assert.equal(timeline.newerOmitted, false);
});

test("a window cut at a turn keeps the preparation records written before the user's message", () => {
  const record = (offset: string, eventType: string, kind: string | null, runId: string | null) => ({
    offset, eventType, providerId: "p", sessionId: "runtime", runId, timestamp: "2026-01-01T00:00:00Z", kind, phase: null,
    contentId: eventType === "contentCompleted" ? offset : null, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: "text", details: null, textTruncated: false, tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false,
  } satisfies HistoryResponse["entries"][number]);
  const revision = { sessionId: "s", length: "900", lastWriteUtcTicks: "900" };
  // A new session: nothing precedes the first turn, so nothing is left to load.
  const first = [record("0", "sessionUpdate", "ModelChanged", "r1"), record("100", "system_prompt", "session_start", "r1"),
    record("200", "contentCompleted", "User", "r1"), record("300", "contentCompleted", "Assistant", "r1")];
  const fresh = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, { status: "ok", entries: first, next: null, tailOmitted: false, revision });
  assert.deepEqual(fresh.entries.map(entry => entry.offset), ["0", "100", "200", "300"]);
  assert.equal(fresh.next, null);
  // A later turn starts at its own model record; the turn before it stays behind the cursor.
  const second = [...first, record("400", "raw", null, null), record("500", "sessionUpdate", "ModelChanged", "r2"),
    record("600", "raw", null, null), record("700", "contentCompleted", "User", "r2"), record("800", "contentCompleted", "Assistant", "r2")];
  const later = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, { status: "ok", entries: second, next: null, tailOmitted: false, revision });
  assert.deepEqual(later.entries.map(entry => entry.offset), ["500", "600", "700", "800"]);
  assert.equal(later.next?.offset, "500");
  // A record of another run before the message is not part of its preparation.
  const foreign = [record("0", "contentCompleted", "Assistant", "r1"), record("100", "sessionUpdate", "ModelChanged", "r1"),
    record("200", "contentCompleted", "User", "r2")];
  const cut = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, { status: "ok", entries: foreign, next: null, tailOmitted: false, revision });
  assert.deepEqual(cut.entries.map(entry => entry.offset), ["200"]);
});
