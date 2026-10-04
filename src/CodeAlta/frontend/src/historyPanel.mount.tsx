// Test-owned mounted production History + scroll hook with isolated journal-like reverse pages.
import { createElement, useCallback, useLayoutEffect, useRef, useState, type UIEvent } from "react";
import { createRoot } from "react-dom/client";
import type { HistoryRequest, HistoryResponse } from "#neoastra";
import { History } from "./HistoryPanel";
import { createTimelineScrollMemory, useExplicitNewestHistory, useTimelinePosition, timelineNotice, type TimelineNotice } from "./timelineScroll";
import { resolveCommandKey } from "./commandRegistry";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";

const memory = createTimelineScrollMemory();
const calls: string[] = [];
let failCode: string | null = null;
let failCursorCode: string | null = null;
let mismatchCursor = false;
let holdNext = false;
let release: (() => void) | undefined;
const stable = () => {};
function row(index: number, session: string): HistoryResponse["entries"][number] {
  return { offset: `${index * 200}`, eventType: index === 1203 ? "sessionUpdate" : "contentCompleted", providerId: "fixture", sessionId: session,
    runId: null, timestamp: "2026-01-01T00:00:00Z", kind: index === 1204 ? "User" : index === 1202 ? "CommandOutput" : "Assistant", phase: null,
    contentId: `${index}`, activityId: null, parentActivityId: null, interactionId: null, name: null,
    text: index === 1204 ? "latest user prompt" : `turn-${index}`, details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false };
}
async function read(request: HistoryRequest): Promise<HistoryResponse> {
  calls.push(`${request.sessionId}:${request.cursor?.version ?? "tail"}:${request.cursor?.offset ?? "end"}`);
  if (holdNext) { holdNext = false; await new Promise<void>(resolve => { release = resolve; }); }
  if (failCode) { const status = failCode; failCode = null; return { status, entries: [], next: null, tailOmitted: false }; }
  if (request.cursor && failCursorCode) { const status = failCursorCode; failCursorCode = null;
    return { status, entries: [], next: null, tailOmitted: false }; }
  const total = request.sessionId === "A" ? 1205 : request.sessionId === "C" ? 200 : 3;
  const end = request.cursor ? Number(request.cursor.offset) / 200 : total;
  const start = Math.max(0, end - 100);
  let next = start ? { version: 2, sessionId: request.sessionId, length: `${total * 200}`, lastWriteUtcTicks: "7",
    offset: `${start * 200}` } : null;
  if (request.cursor && next && mismatchCursor) { mismatchCursor = false; next = { ...next, lastWriteUtcTicks: "8" }; }
  return { status: "ok", entries: request.sessionId === "C" && end === 200 ? [] :
    Array.from({ length: end - start }, (_, i) => row(start + i, request.sessionId)), next, tailOmitted: false };
}
function Mounted({ sessionId, epoch }: { sessionId: string; epoch: string }) {
  const position = useTimelinePosition(sessionId, memory);
  const shell = useRef<HTMLDivElement>(null);
  const chord = useRef(false);
  const [notice, setNotice] = useState("");
  const presentNotice = useCallback((value: TimelineNotice) => setNotice(timelineNotice("en", value)), []);
  const newest = useExplicitNewestHistory(sessionId, null, epoch, position, presentNotice);
  const resetNotice = useCallback((generation: number, explicitNewest: boolean) => {
    position.resetMessageNavigation();
    if (!newest.onTarget(generation)) {
      if (explicitNewest) position.pauseIfUnfollowed();
      setNotice("");
    }
  }, [position.resetMessageNavigation, position.pauseIfUnfollowed, newest.onTarget]);
  useLayoutEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLElement && shell.current?.contains(event.target) &&
        !event.target.closest("input, textarea, select, [contenteditable='true']") &&
        ["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].includes(event.key)) newest.cancel();
      // The production key map, scoped to this mounted workspace; a modal takes the keys, as in the app.
      const target = event.target instanceof HTMLElement ? event.target : null;
      if (!target || !shell.current?.contains(target) || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) { chord.current = false; return; }
      const resolved = resolveCommandKey(event, chord.current, target.closest("#session-prompt, #catalog-prompt") ? "prompt" : "none");
      chord.current = resolved.chord;
      if (!resolved.handled) return;
      event.preventDefault();
      const action = resolved.command;
      if (action === "messageLatest") { if (newest.available()) newest.latest(); return; }
      if (action !== "messagePrevious" && action !== "messageNext" && action !== "messageFirst" || !position.messageReady()) return;
      newest.cancel();
      const result = position.navigateMessage(action);
      setNotice(result.status === "boundary" ? action === "messageNext" ? "Last retained message; refresh newest history."
        : "First retained message; older journal history may exist." : result.label ?? result.status);
    };
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  });
  return createElement("div", { className: "workspace-shell", ref: shell },
    createElement("button", { type: "button", className: "keyboard-target" }, "Timeline keyboard target"),
    createElement("button", { type: "button", className: "follow-target", onClick: () => { newest.cancel(); position.jump(); } }, "Follow visible window"),
    createElement("div", { className: "timeline-scroll", ref: position.elementRef,
    "data-following": position.following,
    onScroll: (event: UIEvent<HTMLDivElement>) => {
      newest.onScroll(); if (!newest.pending()) position.scroll(event.currentTarget);
    },
    onWheel: newest.cancel, onPointerDown: newest.cancel,
    style: { height: "260px", overflowY: "scroll", width: "650px" } },
    createElement(History, { sessionId, read, live: null, onNotesChange: stable,
      onSettled: () => { position.settled(); if (!newest.pending()) position.pauseIfUnfollowed(); },
      onBeforeOlder: position.beforeOlderPage, onAfterOlder: position.afterOlderPage,
      onNavigationReset: resetNotice, newestRequest: newest.requestRef, onNewestResult: newest.onResult })),
    createElement("p", { role: "status", className: "navigation-notice" }, notice),
    createElement("textarea", { id: "session-prompt", "aria-label": "Prompt" }));
}
function Fixture() {
  const [locale, language] = useState<Locale>("en");
  const [session, setSession] = useState("A");
  const [epoch, setEpoch] = useState("fixture");
  useLayoutEffect(() => {
    Object.assign(window, { fixture: { language, select: setSession, host: setEpoch, calls, failNext: (code: string) => { failCode = code; },
      failCursor: (code: string) => { failCursorCode = code; },
      mismatchCursor: () => { mismatchCursor = true; },
      holdNext: () => { holdNext = true; }, release: () => { release?.(); release = undefined; } } });
  }, []);
  return createElement(ShellLanguageContext, { value: { locale, choice: locale, setLanguage: () => {} } },
    // Exercise the production outer access-scroller policy, not an ad-hoc nested scroller.
    createElement("div", { className: "outer-scroll active-session-content", style: { height: "360px", overflowY: "scroll" } },
    createElement(Mounted, { key: session, sessionId: session, epoch }),
    createElement("button", { type: "button", className: "outside-target" }, "Outside workspace"),
    createElement("div", { style: { height: "600px" } }, "Outer filler")));
}
const style = document.createElement("style");
// This isolated fixture has no project/session rails: its shortcut scope is not the App's three-column grid.
style.textContent = ".outer-scroll > .workspace-shell { display: block; } .timeline-message { height: 48px; box-sizing: border-box; overflow: hidden; } .message-body p { margin: 0; }";
document.head.append(style);
createRoot(document.getElementById("app")!).render(createElement(Fixture));
