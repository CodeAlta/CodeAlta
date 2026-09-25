// Test-owned mounted production History + scroll hook with isolated journal-like reverse pages.
import { createElement, useCallback, useLayoutEffect, useRef, useState, type UIEvent } from "react";
import { createRoot } from "react-dom/client";
import type { HistoryRequest, HistoryResponse } from "#neoastra";
import { History } from "./HistoryPanel";
import { createTimelineScrollMemory, useTimelinePosition } from "./timelineScroll";
import { dispatchWorkspaceShortcut, type WorkspaceShortcutState } from "./workspaceShortcutDispatch";

const memory = createTimelineScrollMemory();
const calls: string[] = [];
let failCode: string | null = null;
let holdNext = false;
let release: (() => void) | undefined;
const stable = () => {};
function row(index: number, session: string): HistoryResponse["entries"][number] {
  return { offset: `${index * 200}`, eventType: index === 1203 ? "sessionUpdate" : "contentCompleted", providerId: "fixture", sessionId: session,
    runId: null, timestamp: "2026-01-01T00:00:00Z", kind: index === 1204 ? "User" : index === 1202 ? "CommandOutput" : "Assistant", phase: null,
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
  const shell = useRef<HTMLDivElement>(null);
  const shortcut = useRef<WorkspaceShortcutState>({ chordPending: false, sessionInfoPrefix: null, reminderPrefix: null });
  const [notice, setNotice] = useState("");
  const resetNotice = useCallback(() => { position.resetMessageNavigation(); setNotice(""); }, [position.resetMessageNavigation]);
  useLayoutEffect(() => {
    const keyDown = (event: KeyboardEvent) => dispatchWorkspaceShortcut(event, shortcut.current, {
      workspaceActive: true, workspaceShell: shell.current, modalOpen: false, selectedProjectFocused: false,
      infoTrigger: null, reminderTrigger: null, infoSelection: { sessionId, projectId: null }, selection: null,
      messageAvailable: position.messageReady(),
      run: action => {
        if (action !== "messagePrevious" && action !== "messageNext" && action !== "messageFirst") return;
        const result = position.navigateMessage(action);
        setNotice(result.status === "boundary" ? action === "messageNext" ? "Last retained message; refresh newest history."
          : "First retained message; older journal history may exist." : result.label ?? result.status);
      },
    });
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  });
  return createElement("div", { className: "workspace-shell", ref: shell },
    createElement("button", { type: "button", className: "keyboard-target" }, "Timeline keyboard target"),
    createElement("button", { type: "button", className: "follow-target", onClick: position.jump }, "Follow visible window"),
    createElement("div", { className: "timeline-scroll", ref: position.elementRef,
    "data-following": position.following,
    onScroll: (event: UIEvent<HTMLDivElement>) => position.scroll(event.currentTarget),
    style: { height: "260px", overflowY: "scroll", width: "650px" } },
    createElement(History, { sessionId, read, live: null, onNotesChange: stable,
      onSettled: position.settled, onBeforeOlder: position.beforeOlderPage, onAfterOlder: position.afterOlderPage,
      onNavigationReset: resetNotice })),
    createElement("p", { role: "status", className: "navigation-notice" }, notice),
    createElement("textarea", { id: "session-prompt", "aria-label": "Prompt" }));
}
function Fixture() {
  const [session, setSession] = useState("A");
  useLayoutEffect(() => {
    Object.assign(window, { fixture: { select: setSession, calls, failNext: (code: string) => { failCode = code; },
      holdNext: () => { holdNext = true; }, release: () => { release?.(); release = undefined; } } });
  }, []);
  return createElement("div", { className: "outer-scroll", style: { height: "360px", overflowY: "scroll" } },
    createElement(Mounted, { key: session, sessionId: session }),
    createElement("button", { type: "button", className: "outside-target" }, "Outside workspace"),
    createElement("div", { style: { height: "600px" } }, "Outer filler"));
}
const style = document.createElement("style");
style.textContent = ".timeline-message { height: 48px; box-sizing: border-box; overflow: hidden; } .message-body p { margin: 0; }";
document.head.append(style);
createRoot(document.getElementById("app")!).render(createElement(Fixture));
