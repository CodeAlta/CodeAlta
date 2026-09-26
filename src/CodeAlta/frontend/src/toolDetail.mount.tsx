import { createElement, useCallback, useLayoutEffect, useRef, useState,
  type PointerEvent, type UIEvent, type WheelEvent } from "react";
import { createRoot } from "react-dom/client";
import type { HistoryRequest, HistoryResponse } from "#neoastra";
import { History } from "./HistoryPanel";
import { createTimelineScrollMemory, useExplicitNewestHistory, useTimelinePosition } from "./timelineScroll";
import { dispatchWorkspaceShortcut, type WorkspaceShortcutState } from "./workspaceShortcutDispatch";
import { MarkdownContent } from "./MarkdownContent";
import { LiveTextMessage, LiveToolMessage } from "./LiveSessionPanel";

function createFixture() {
  const memory = createTimelineScrollMemory();
  const calls: string[] = [];
  const longLine = "tool-diagnostic-" + "X".repeat(1200);
  let hold = false;
  let longBodies = false;
  let changedBody = false;
  let assistantOverride: Partial<HistoryResponse["entries"][number]> = {};
  const codeText = Array.from({ length: 35 }, (_, i) => `line-${i + 1} ${"界🙂".repeat(12)}`).join("\n") + "\n" + "界🙂".repeat(90);
  const codeMarkdown = `\`\`\`ts\n${codeText}\n\`\`\`\n\n${codeText.split("\n").map(line => `    ${line}`).join("\n")}\n\n<script>window.codeInjected=true</script>`;
  let codeSuffix = "";
  let toolMessage = `  Supplied message\r\n\n${codeMarkdown}\n\n<img src=x onerror="window.messageInjected=true">\n  MESSAGE END  `;
  let release: (() => void) | undefined;
  function row(index: number, session: string): HistoryResponse["entries"][number] {
    const omitted = session === "B" && index === 1;
    const tool = index === 0 || index === 1204 || omitted;
    const longUser = longBodies && session === "A" && index === 1201;
    const shortUser = longBodies && session === "A" && index === 1203;
    const longAssistant = longBodies && index === 700;
    const longText = `**First** [reference](https://example.invalid/path)\n\n\`\`\`js\nconst answer = 42;\n\`\`\`\n${"plain text ".repeat(135)}<img src=x onerror="window.toolFixture.injected=true"> FULL END`;
    return { offset: `${index * 200}`, eventType: tool ? "activity" : "contentCompleted", providerId: "fixture", sessionId: session,
      runId: "test-run", timestamp: "2026-01-01T00:00:00Z", kind: tool ? "ToolCall" : longUser || shortUser ? "User" : "Assistant", phase: tool ? "failed" : null,
      contentId: tool ? null : `${index}`, activityId: tool ? `tool-${index}` : null, parentActivityId: null, interactionId: null,
      name: tool ? "fixture_tool" : null, text: omitted ? null : tool ? "**Failed** `literal code stays as Markdown`" :
        longUser || longAssistant ? `${longText} ${changedBody ? "changed " : ""}${session}-${index}` : `turn-${index}`,
      details: tool && !omitted ? JSON.stringify({ command: longLine, result: { output: `${longLine}\n${longLine}\n<img src=x onerror=alert(1)>` } }) : null,
      textTruncated: longUser, detailsTruncated: tool && !omitted, bodyOmitted: tool || longUser,
      ...(longAssistant ? assistantOverride : {}),
      ...(session === "D" ? { phase: "Canceled", text: toolMessage, textTruncated: true } : {}),
      ...(session === "C" && index > 0 ? { text: index === 1 ? "```\nshort\n```" : codeMarkdown + (index === 2 ? codeSuffix : ""),
        ...(index === 3 ? { eventType: "system_prompt" } : {}) } : {}) };
  }
  async function read(request: HistoryRequest): Promise<HistoryResponse> {
    calls.push(`${request.sessionId}:${request.cursor?.offset ?? "tail"}`);
    if (hold) { hold = false; await new Promise<void>(resolve => { release = resolve; }); }
    const total = request.sessionId === "D" ? 1 : request.sessionId === "C" ? 4 : request.sessionId === "A" || longBodies ? 1205 : 3;
    const end = request.cursor ? Number(request.cursor.offset) / 200 : total;
    const start = Math.max(0, end - 100);
    return { status: "ok", entries: Array.from({ length: end - start }, (_, i) => row(start + i, request.sessionId)),
      next: start ? { version: 2, sessionId: request.sessionId, length: `${total * 200}`,
        lastWriteUtcTicks: "7", offset: `${start * 200}` } : null, tailOmitted: false };
  }
  function Mounted({ sessionId }: { sessionId: string }) {
    const [, rerender] = useState(0);
    const position = useTimelinePosition(sessionId, memory);
    const shell = useRef<HTMLDivElement>(null);
    const chord = useRef<WorkspaceShortcutState>({ chordPending: false, sessionInfoPrefix: null, reminderPrefix: null });
    const [notice, setNotice] = useState("");
    const [deferredHeight, setDeferredHeight] = useState(0);
    const newest = useExplicitNewestHistory(sessionId, null, "fixture-epoch", position, setNotice);
    const reset = useCallback((generation: number, explicitNewest: boolean) => {
      position.resetMessageNavigation();
      if (!newest.onTarget(generation)) {
        if (explicitNewest) position.pauseIfUnfollowed();
        setNotice("");
      }
    }, [position.resetMessageNavigation, position.pauseIfUnfollowed, newest.onTarget]);
    useLayoutEffect(() => {
      const fixture = Object.assign((window as Window & { toolFixture?: object }).toolFixture ?? {}, {
        select: (id: string) => window.dispatchEvent(new CustomEvent("tool-select", { detail: id })),
        grow: setDeferredHeight, calls, enableLong: () => { longBodies = true; }, changeBody: () => { changedBody = true; },
        replaceAssistant: (value: typeof assistantOverride) => { assistantOverride = value; },
        codeText, codeMarkdown, changeCode: () => { codeSuffix = "\nChanged source"; },
        toolMessage, replaceToolMessage: (value: string) => { toolMessage = value; },
        rerender: () => rerender(value => value + 1),
        hold: () => { hold = true; }, release: () => { release?.(); release = undefined; } });
      Object.assign(window, { toolFixture: fixture });
    }, []);
    useLayoutEffect(() => {
      const keyDown = (event: KeyboardEvent) => {
        dispatchWorkspaceShortcut(event, chord.current, { workspaceActive: true, workspaceShell: shell.current,
          modalOpen: false, selectedProjectFocused: false, infoTrigger: null, reminderTrigger: null,
          infoSelection: { sessionId, projectId: null }, selection: null,
          messageAvailable: position.messageReady(), latestAvailable: newest.available(),
          run: action => { if (action === "messageLatest") newest.latest(); }, });
      };
      window.addEventListener("keydown", keyDown);
      return () => window.removeEventListener("keydown", keyDown);
    });
    return createElement("div", { className: "fixture-workspace", ref: shell, style: { width: "100%", minWidth: 0, overflow: "hidden" } },
      createElement("button", { className: "keyboard-target", type: "button" }, "Timeline keyboard target"),
      createElement("div", { className: "timeline-scroll", ref: position.elementRef, "data-following": position.following,
        onScroll: (event: UIEvent<HTMLDivElement>) => { newest.onScroll(); if (!newest.pending()) position.scroll(event.currentTarget); },
        onWheel: (event: WheelEvent<HTMLDivElement>) => { newest.cancel(); position.wheel(event); },
        onKeyDown: position.keyDown,
        onPointerDown: (event: PointerEvent<HTMLDivElement>) => { newest.cancel(); position.pointerDown(event); },
        onPointerMove: position.pointerMove, onPointerUp: position.pointerEnd, onPointerCancel: position.pointerEnd,
        style: { height: "260px", overflowY: "scroll", width: "100%" } },
        createElement(History, { sessionId, read, live: null, onNotesChange: () => {},
          onSettled: () => { position.settled(); if (!newest.pending()) position.pauseIfUnfollowed(); },
          onBeforeOlder: position.beforeOlderPage, onAfterOlder: position.afterOlderPage,
          onNavigationReset: reset, newestRequest: newest.requestRef, onNewestResult: newest.onResult }),
        createElement("div", { className: "deferred-layout", style: { height: deferredHeight } })),
      createElement("p", { role: "status", className: "navigation-notice" }, notice),
      sessionId === "D" && createElement("div", { className: "unchanged-live-tool" },
        createElement(LiveToolMessage, { row: { providerId: "literal-provider", runId: null, activityId: "tool-0",
          phase: "Completed", name: "Literal name", isNameTruncated: true } })),
      sessionId === "C" && createElement("div", { className: "unchanged-markdown" },
        createElement(MarkdownContent, { source: codeMarkdown }),
        createElement(LiveTextMessage, { row: { runId: "literal", contentId: "code", kind: "Assistant", text: codeMarkdown,
          isComplete: false, isTruncated: false, startedWithDelta: false } })));
  }
  function Fixture() {
    const [session, setSession] = useState("A");
    useLayoutEffect(() => {
      const select = (event: Event) => setSession((event as CustomEvent<string>).detail);
      window.addEventListener("tool-select", select);
      return () => window.removeEventListener("tool-select", select);
    }, []);
    return createElement(Mounted, { key: session, sessionId: session });
  }
  return Fixture;
}
createRoot(document.getElementById("app")!).render(createElement(createFixture()));
