import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { flushSync } from "react-dom";
import type { HistoryRequest, HistoryResponse, SessionDisplayText, SessionDisplayView } from "#neoastra";
import { History } from "./HistoryPanel";

// Production History owns the live row keys. No host, subscription, clipboard or journal is opened.
function createFixture() {
  const root = createRoot(document.getElementById("app")!);
  let sessionId = "fixture-session";
  let visible = true;
  let reads = 0;
  let row: SessionDisplayText = { runId: "run-one", contentId: "content-one", kind: "Assistant",
    text: "**Retained** 😀 [reference](https://example.invalid/)\n\n<script>window.liveCopyInjected=true</script>\n\n" + "stream text ".repeat(140) + "\nFULL OLD END",
    isComplete: false, isTruncated: true, startedWithDelta: true };
  const read = async (_request: HistoryRequest): Promise<HistoryResponse> => {
    reads++;
    return { status: "ok", entries: [], next: null, tailOmitted: false };
  };
  const noop = () => {};
  function render() {
    const live: SessionDisplayView = { sessionId, revision: "1", lifecycle: null, queuedPromptCount: null,
      configuration: null, statusKind: null, statusMessage: null, text: [row], toolActivities: [],
      metadataTruncated: false, transportTruncated: false, evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0" };
    flushSync(() => root.render(createElement(History, { key: sessionId, sessionId, read, live: visible ? live : null,
      onNotesChange: noop, onSettled: noop, onBeforeOlder: noop, onAfterOlder: noop })));
  }
  return {
    render, text: () => row.text, reads: () => reads,
    update(value: Partial<SessionDisplayText>) { row = { ...row, ...value }; render(); },
    show(value: boolean) { visible = value; render(); },
    select(value: string) { sessionId = value; render(); },
  };
}

const fixture = createFixture();
Object.assign(window, { liveCopyFixture: fixture });
fixture.render();
