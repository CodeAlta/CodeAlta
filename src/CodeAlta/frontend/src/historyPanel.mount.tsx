// Test-owned mounted production History + scroll hook with isolated journal-like reverse pages.
import { createElement, useLayoutEffect, useState, type UIEvent } from "react";
import { createRoot } from "react-dom/client";
import type { HistoryRequest, HistoryResponse } from "#neoastra";
import { History } from "./HistoryPanel";
import { createTimelineScrollMemory, useTimelinePosition } from "./timelineScroll";

const memory = createTimelineScrollMemory();
const calls: string[] = [];
let failCode: string | null = null;
let holdNext = false;
let release: (() => void) | undefined;
const stable = () => {};
function row(index: number, session: string): HistoryResponse["entries"][number] {
  return { offset: `${index * 200}`, eventType: "contentCompleted", providerId: "fixture", sessionId: session,
    runId: null, timestamp: "2026-01-01T00:00:00Z", kind: index === 1204 ? "User" : "Assistant", phase: null,
    contentId: `${index}`, activityId: null, parentActivityId: null, interactionId: null, name: null,
    text: index === 1204 ? "latest user prompt" : `turn-${index}`, details: null,
    textTruncated: false, detailsTruncated: false, bodyOmitted: false };
}
async function read(request: HistoryRequest): Promise<HistoryResponse> {
  calls.push(`${request.sessionId}:${request.cursor?.version ?? "tail"}:${request.cursor?.offset ?? "end"}`);
  if (holdNext) { holdNext = false; await new Promise<void>(resolve => { release = resolve; }); }
  if (failCode) { const status = failCode; failCode = null; return { status, entries: [], next: null, tailOmitted: false }; }
  const total = request.sessionId === "A" ? 1205 : request.sessionId === "C" ? 200 : 3;
  const end = request.cursor ? Number(request.cursor.offset) / 200 : total;
  const start = Math.max(0, end - 100);
  const next = start ? { version: 2, sessionId: request.sessionId, length: `${total * 200}`, lastWriteUtcTicks: "7",
    offset: `${start * 200}` } : null;
  return { status: "ok", entries: request.sessionId === "C" && end === 200 ? [] :
    Array.from({ length: end - start }, (_, i) => row(start + i, request.sessionId)), next, tailOmitted: false };
}
function Mounted({ sessionId }: { sessionId: string }) {
  const position = useTimelinePosition(sessionId, memory);
  return createElement("div", { className: "timeline-scroll", ref: position.elementRef,
    "data-following": position.following,
    onScroll: (event: UIEvent<HTMLDivElement>) => position.scroll(event.currentTarget),
    style: { height: "260px", overflowY: "scroll", width: "650px" } },
    createElement(History, { sessionId, read, live: null, onNotesChange: stable,
      onSettled: position.settled, onBeforeOlder: position.beforeOlderPage, onAfterOlder: position.afterOlderPage }));
}
function Fixture() {
  const [session, setSession] = useState("A");
  useLayoutEffect(() => {
    Object.assign(window, { fixture: { select: setSession, calls, failNext: (code: string) => { failCode = code; },
      holdNext: () => { holdNext = true; }, release: () => { release?.(); release = undefined; } } });
  }, []);
  return createElement(Mounted, { key: session, sessionId: session });
}
const style = document.createElement("style");
style.textContent = ".timeline-message { height: 48px; box-sizing: border-box; overflow: hidden; } .message-body p { margin: 0; }";
document.head.append(style);
createRoot(document.getElementById("app")!).render(createElement(Fixture));
