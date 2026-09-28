import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { loadHistorySource } from "./loadHistorySource";
import { historyEntryCharacters, historySettled, loadHistory, mergeHistoryPage, type HistoryTimeline, type HistoryState } from "./history";
import type { HistoryResponse, HistorySourceRequest, HistorySourceResponse } from "#neoastra";
import { locales, translate } from "./localization";

const request: HistorySourceRequest = { revision: { sessionId: "s", length: "4000000", lastWriteUtcTicks: "7" }, start: "0", end: "4000000", offset: "0" };

test("source chunks are bounded and stale completion cannot publish or retarget", async () => {
  const abort = new AbortController();
  const results: HistorySourceResponse[] = [];
  let current = true;
  await loadHistorySource(async (actual) => { assert.equal(actual, request); return { status: "ok", text: "日🙂", nextOffset: "7" }; }, request, abort.signal, () => current, value => results.push(value));
  assert.equal(results[0].text, "日🙂");
  const pending = loadHistorySource(async () => { current = false; return { status: "ok", text: "stale", nextOffset: "10" }; }, request, abort.signal, () => current, value => results.push(value));
  await pending; assert.equal(results.length, 1);
  current = true;
  for (const value of [{ status: "ok", text: "x".repeat(16385), nextOffset: null }, { status: "ok", text: "x", nextOffset: "0" }]) {
    await loadHistorySource(async () => value, request, abort.signal, () => current, value => results.push(value));
    assert.equal(results.at(-1)?.status, "wire_limit");
  }
  abort.abort();
  await loadHistorySource(async () => { assert.fail("canceled request admitted"); }, request, abort.signal, () => true, value => results.push(value));
});

test("source fixture forwards exact revision/range/options and suppresses aborted or retired late results", async () => {
  for (const mode of ["abort", "retire", "failure"] as const) {
    const abort = new AbortController(); let active = true;
    let resolve!: (value: HistorySourceResponse) => void, reject!: (error: Error) => void;
    const results: HistorySourceResponse[] = [];
    const pending = loadHistorySource((actual, options) => {
      assert.equal(actual, request); assert.equal(options.signal, abort.signal); assert.equal(options.timeoutMilliseconds, 30_000);
      return new Promise((yes, no) => { resolve = yes; reject = no; });
    }, request, abort.signal, () => active, value => results.push(value));
    if (mode === "abort") abort.abort(); else active = false;
    if (mode === "failure") reject(new Error("late fixture failure"));
    else resolve({ status: "ok", text: "literal source", nextOffset: "14" });
    await pending; assert.deepEqual(results, []);
  }
  const results: HistorySourceResponse[] = [];
  for (const status of ["history_changed", "missing_session", "outside_root", "unsupported_format", "read_failed"]) {
    await loadHistorySource(async () => ({ status, text: null, nextOffset: null }), request,
      new AbortController().signal, () => true, value => results.push(value));
    assert.equal(results.at(-1)?.status, status);
  }
});

test("source controls retain production lifetime, revision, modal, input and copy-chunk fences", () => {
  const source = readFileSync(new URL("./HistorySource.tsx", import.meta.url), "utf8");
  assert.match(source, /original\.current\?\.\(\)/); assert.match(source, /latest\.current\?\.\(\)/);
  assert.match(source, /controller\.current === read && !read\.signal\.aborted/);
  assert.match(source, /retire\(\); onClose\(\)/); assert.match(source, /instanceof HTMLDialogElement/);
  assert.match(source, /isConnected/); assert.match(source, /closest\("\[inert\]"\)/);
  assert.match(source, /event\.nativeEvent\.isComposing/); assert.match(source, /event\.repeat/);
  const panel = readFileSync(new URL("./HistoryPanel.tsx", import.meta.url), "utf8");
  assert.match(panel, /generation\.current === captured/);
  assert.match(panel, /JSON\.stringify\(sourceTarget\.revision\) === JSON\.stringify\(timeline\?\.revision\)/);
  const adapter = readFileSync(new URL("./readTimeline.ts", import.meta.url), "utf8");
  assert.match(adapter, /workspace\.historyTimeline\(request, options\)/);
  assert.match(adapter, /revision: value\.revision, sources: value\.sources/);
  const fixture = readFileSync(new URL("./settingsShell.neoastra.mount.ts", import.meta.url), "utf8");
  assert.match(fixture, /page: await workspace\.historyTail\(request\)/);
  assert.match(fixture, /historySource: async/);
  for (const key of ["Full raw journal record", "Close source", "First chunk", "Next chunk", "Copy chunk", "Chunk copied",
    "Read full raw record (paged)", "Source review expired. Close and reopen from the history row."] as const) {
    for (const locale of locales) if (locale !== "en") assert.notEqual(translate(locale, key), key);
  }
});

function entry(index: number): HistoryResponse["entries"][number] {
  return { offset: String(index * 200), eventType: "contentCompleted", providerId: "p", sessionId: "s", runId: null,
    timestamp: "2026-01-01T00:00:00Z", kind: "Assistant", phase: null, contentId: String(index), activityId: null,
    parentActivityId: null, interactionId: null, name: null, text: "x".repeat(32768), details: null,
    files: null, textTruncated: true, detailsTruncated: false, bodyOmitted: false };
}

test("large pages bound retained text, preserve newest, and explicitly slide older without losing cursor", () => {
  let timeline: HistoryTimeline | undefined;
  for (let end = 100; end > 0 && !timeline?.limitReached; end -= 10) {
    const start = end - 10;
    timeline = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline?.next ?? null }, { status: "ok",
      entries: Array.from({ length: 10 }, (_, i) => entry(start + i)), next: { version: 2, sessionId: "s", length: "9999999", lastWriteUtcTicks: "7", offset: String(start * 200) }, tailOmitted: false });
  }
  assert.ok(timeline?.limitReached);
  assert.ok(timeline.entries.reduce((total, row) => total + historyEntryCharacters(row), 0) <= 2 * 1024 * 1024);
  assert.equal(timeline.entries.at(-1)?.contentId, "99");
  assert.equal(timeline.next?.offset, timeline.entries[0].offset);
  const older = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline.next }, { status: "ok", entries: [entry(1)], next: null, tailOmitted: false }, true);
  assert.equal(older.entries[0].contentId, "1"); assert.equal(older.newerOmitted, true);
});

test("metadata-only pages stop after a finite acquisition and later errors do not replace healthy rows", async () => {
  let timeline: HistoryTimeline | undefined;
  const page = { status: "ok", entries: [entry(999)], next: { version: 2, sessionId: "s", length: "9999999", lastWriteUtcTicks: "7", offset: "199800" }, tailOmitted: false };
  timeline = mergeHistoryPage(undefined, { sessionId: "s", cursor: null }, page);
  for (let i = 1; i < 32; i++) timeline = mergeHistoryPage(timeline, { sessionId: "s", cursor: timeline.next }, { ...page, entries: [], next: { ...page.next, offset: String(199800 - i) } });
  assert.equal(timeline.pages, 32); assert.equal(timeline.limitReached, true);
  assert.equal(historySettled({ kind: "ready", request: { sessionId: "s", cursor: timeline.next }, page }, timeline), true);
  const original = timeline;
  const states: HistoryState[] = [];
  await loadHistory(async () => ({ ...page, status: "history_changed", entries: [], next: null }), { sessionId: "s", cursor: timeline.next }, new AbortController().signal, value => states.push(value));
  assert.equal(states.at(-1)?.kind, "error"); assert.equal(timeline, original);
  const source = readFileSync(new URL("./HistoryPanel.tsx", import.meta.url), "utf8");
  assert.doesNotMatch(source, /setWindow\(undefined\)/);
  assert.match(source, /accumulated\.limitReached \|\| target\.explicitOlder/);
  assert.match(source, /Previously loaded history is retained/);
  const inspector = readFileSync(new URL("./HistorySource.tsx", import.meta.url), "utf8");
  assert.match(inspector, /<pre /); assert.doesNotMatch(inspector, /MarkdownContent|dangerouslySetInnerHTML/);
});
