import test from "node:test";
import assert from "node:assert/strict";
import type { HistoryResponse, SessionPluginEvent } from "#neoastra";
import { createPluginEventsRead, pluginEventItem, pluginEventItems, pluginEventsWindow } from "./pluginEvents";

type Entry = HistoryResponse["entries"][number];
const entry = (offset: string, patch: Partial<Entry>): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider", sessionId: "session", runId: "run",
  timestamp: `2026-01-01T10:00:${offset.padStart(2, "0")}Z`, kind: "Assistant", phase: null, contentId: null, activityId: null, parentActivityId: null,
  interactionId: null, sourceSessionId: null, name: null, text: "text", details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, files: null, tool: null, images: null, ...patch });
const idle = (offset: string) => entry(offset, { eventType: "sessionUpdate", kind: "Idle", text: null });
const card = (patch: Partial<SessionPluginEvent> = {}): SessionPluginEvent => ({ eventId: "statistics:session:run-1", pluginId: "statistics", timestamp: "2026-01-01T10:00:09Z",
  markdown: "**Turn statistics** · 4.0s · tools 2 calls / 1.5s", details: [{ header: "Detailed statistics", markdown: "| Metric | Value |\n| --- | ---: |", html: null }], html: null, ...patch });

test("cards are read again when a turn ends or older history is loaded, not while a turn streams", () => {
  assert.equal(pluginEventsWindow([]), null);
  assert.equal(pluginEventsWindow([entry("1", { kind: "User" }), entry("2", {})]), null, "no finished turn, no cards");
  const ended = [entry("1", { kind: "User" }), entry("2", {}), idle("3")];
  const window = pluginEventsWindow(ended);
  assert.deepEqual(window, { key: "1:3", notBefore: "2026-01-01T10:00:01Z" });
  assert.deepEqual(pluginEventsWindow([...ended, entry("4", { kind: "User", runId: "next" }), entry("5", { runId: "next" })]), window, "a running turn changes nothing");
  assert.equal(pluginEventsWindow([...ended, entry("4", { runId: "next" }), idle("5")])?.key, "1:5");
  assert.equal(pluginEventsWindow([entry("0", {}), ...ended])?.key, "0:3");
  assert.equal(pluginEventsWindow([entry("1", {}), entry("2", { eventType: "error" })])?.key, "1:2");
  assert.equal(pluginEventsWindow([entry("1", {}), entry("2", { eventType: "activity", kind: "Turn", phase: "Completed" })])?.key, "1:2");
  assert.equal(pluginEventsWindow([entry("1", {}), entry("2", { eventType: "activity", kind: "Turn", phase: "Started" })]), null);
});

test("a card is a compact row titled by its leading bold phrase, with its sections as details", () => {
  const item = pluginEventItem(card());
  assert.equal(item.category, "plugin");
  assert.equal(item.key, "plugin:statistics:session:run-1");
  assert.equal(item.title, "Turn statistics");
  assert.equal(item.summary, "4.0s · tools 2 calls / 1.5s");
  assert.equal(item.markdown, null);
  assert.equal(item.detailMarkdown, "| Metric | Value |\n| --- | ---: |");
  assert.equal(item.detailsLabel, "Detailed statistics");
  assert.equal(item.timestamp, "2026-01-01T10:00:09Z");
  assert.ok(item.copyMarkdown?.startsWith("**Turn statistics** · 4.0s"));

  const plain = pluginEventItem(card({ pluginId: "notes", markdown: "Something happened", details: [] }));
  assert.equal(plain.title, "notes");
  assert.equal(plain.summary, "Something happened");
  assert.equal(plain.detailMarkdown, null);

  const sections = pluginEventItem(card({ details: [{ header: "One", markdown: "a", html: null }, { header: "Two", markdown: "b", html: null }] }));
  assert.equal(sections.detailMarkdown, "### One\n\na\n\n### Two\n\nb");

  // A card or a section with an HTML fragment is shown as HTML; Copy still takes the Markdown.
  const rich = pluginEventItem(card({ pluginId: "source:Sample", html: "<b>4.0s</b>",
    details: [{ header: "One", markdown: "a", html: "<i>a</i>" }, { header: "Two", markdown: "b", html: null }] }));
  assert.equal(rich.html, "<b>4.0s</b>");
  assert.equal(rich.pluginKey, "source:Sample");
  assert.deepEqual(rich.detailSections, [{ header: "One", html: "<i>a</i>", markdown: null }, { header: "Two", html: null, markdown: "b" }]);
  assert.equal(rich.detailMarkdown, null);
  assert.equal(sections.detailSections, undefined);
  assert.ok(rich.copyMarkdown?.endsWith("### One\n\na\n\n### Two\n\nb"));
});

test("only cards of turns inside the loaded window are shown", () => {
  const events = [card({ eventId: "old", timestamp: "2026-01-01T09:00:00Z" }), card({ eventId: "new" })];
  assert.deepEqual(pluginEventItems(events, "2026-01-01T10:00:00Z").map(item => item.key), ["plugin:new"]);
  assert.equal(pluginEventItems(events, "2026-01-01T08:00:00Z").length, 2);
  assert.deepEqual(pluginEventItems(events, undefined), []);
});

test("the reader names its session and drops anything but a well-formed answer for it", async () => {
  const requests: unknown[] = [];
  let reply: unknown = { status: "ok", sessionId: "session", events: [card()] };
  const read = createPluginEventsRead(async request => { requests.push(request); return reply; }, { epoch: "epoch", sessionId: "session", projectId: "project" });
  const signal = new AbortController().signal;
  assert.deepEqual(await read("2026-01-01T10:00:00Z", signal), [card()]);
  assert.deepEqual(requests, [{ expectedHostEpoch: "epoch", sessionId: "session", projectId: "project", notBefore: "2026-01-01T10:00:00Z" }]);
  reply = { status: "history_changed", sessionId: "session", events: [] };
  assert.equal(await read("2026-01-01T10:00:00Z", signal), "retry");
  for (const bad of [null, { status: "read_failed", sessionId: "session", events: [] }, { status: "ok", sessionId: "other", events: [] },
    { status: "ok", sessionId: "session", events: [card({ timestamp: "never" })] }, { status: "ok", sessionId: "session", events: [{ ...card(), details: "x" }] },
    { status: "ok", sessionId: "session", events: Array.from({ length: 33 }, () => card()) }]) {
    reply = bad;
    assert.equal(await read("2026-01-01T10:00:00Z", signal), null);
  }
});
