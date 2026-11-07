import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type PointerEvent } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { modalDialogOpen } from "../modalDialogs";
import { useShellLanguage } from "../shellLanguage";
import { TimelineGroupView } from "../TimelineEntryGroup";
import { emphasisProperties } from "../timelineEmphasis/timelineEmphasis";
import { useTimelineEmphasis } from "../timelineEmphasis/useTimelineEmphasis";
import type { TimelineGroup } from "../toolGroups";
import type { SideQuestionEntry, SideQuestions } from "./sideQuestions";
import "./sideQuestion.css";

/** Use the same live rows and appearance settings as the session timeline, without journal actions. */
function SideQuestionTimeline({ entry, sessionId }: { entry: SideQuestionEntry; sessionId: string }) {
  const [emphasis] = useTimelineEmphasis();
  const isolation = useMemo(() => ({ sources: new Map(), echoImages: new Map(), canInspect: () => false, noop: () => {} }), []);
  const group = (kind: "User" | "Assistant", text: string): TimelineGroup => {
    const key = `${entry.id}:${kind}`;
    return { key, tools: false, rows: [{ source: "liveText", key, row: { runId: String(entry.id), contentId: kind, kind, text,
      timestamp: null, sequence: null, isComplete: kind === "User" || entry.state !== "running", isTruncated: false, startedWithDelta: kind === "Assistant" } }] };
  };
  return <div className={`messages${emphasis.enabled ? " timeline-appearance-enabled" : ""}`} style={emphasisProperties(emphasis) as CSSProperties}>
    <TimelineGroupView sessionId={sessionId} group={group("User", entry.question)} entryType="prompt" revision={null} {...isolation} onOpenSource={isolation.noop} onOpenTool={isolation.noop} />
    {entry.answer && <TimelineGroupView sessionId={sessionId} group={group("Assistant", entry.answer)} entryType="answer" revision={null} {...isolation} onOpenSource={isolation.noop} onOpenTool={isolation.noop} />}
  </div>;
}

/** A transient answer over this session's timeline; the composer and running turn remain in place. */
export function SideQuestionPanel({ store, sessionId, active }: { store: SideQuestions; sessionId: string; active: boolean }) {
  const { t } = useShellLanguage();
  const [height, setHeight] = useState(50);
  const panel = useRef<HTMLElement>(null);
  const grip = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: number; y: number; height: number } | null>(null);
  const view = useSyncExternalStore(store.subscribe, () => store.view(sessionId));
  const visible = view.listing || view.shown !== null;
  useEffect(() => {
    const stop = () => {
      const id = drag.current?.id;
      drag.current = null;
      if (id !== undefined && grip.current?.hasPointerCapture(id)) grip.current.releasePointerCapture(id);
    };
    window.addEventListener("blur", stop);
    return () => { window.removeEventListener("blur", stop); stop(); };
  }, []);
  useEffect(() => {
    if (!active || !visible) return;
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented || event.isComposing || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey || modalDialogOpen()) return;
      event.preventDefault(); event.stopPropagation();
      store.close(sessionId);
    };
    window.addEventListener("keydown", escape, true);
    return () => window.removeEventListener("keydown", escape, true);
  }, [active, visible, store, sessionId]);
  if (!visible) return null;
  function resize(pixels: number) {
    const available = panel.current?.parentElement?.clientHeight ?? 0;
    if (available <= 8) return;
    const max = available - 8;
    setHeight(100 * Math.max(Math.min(120, max), Math.min(max, pixels)) / available);
  }
  function end(event: PointerEvent<HTMLDivElement>) {
    if (drag.current?.id !== event.pointerId) return;
    drag.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  const entry = view.entries.find(item => item.id === view.shown);
  return <section ref={panel} className="side-question" aria-label={t("Side question")} style={{ height: `${height}%` }}>
    <div ref={grip} className="side-question-grip" role="separator" aria-label={t("Resize side question")} aria-orientation="horizontal"
      aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(height)} tabIndex={0}
      title={t("Drag to resize; Arrow Up enlarges; Arrow Down shrinks; Home resets")}
      onPointerDown={event => {
        if (!event.isPrimary || event.button !== 0 || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
        drag.current = { id: event.pointerId, y: event.clientY, height: panel.current?.getBoundingClientRect().height ?? 0 };
        event.currentTarget.focus(); event.currentTarget.setPointerCapture(event.pointerId); event.preventDefault();
      }}
      onPointerMove={event => {
        if (drag.current?.id === event.pointerId && event.currentTarget.hasPointerCapture(event.pointerId))
          resize(drag.current.height + drag.current.y - event.clientY);
      }} onPointerUp={end} onPointerCancel={end} onLostPointerCapture={() => { drag.current = null; }}
      onDoubleClick={() => setHeight(50)} onKeyDown={event => {
        if (event.key !== "ArrowUp" && event.key !== "ArrowDown" && event.key !== "Home") return;
        if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
        event.preventDefault(); event.stopPropagation();
        if (event.key === "Home") setHeight(50);
        else resize((panel.current?.getBoundingClientRect().height ?? 0) + (event.key === "ArrowUp" ? 16 : -16));
      }}><span /></div>
    <header><strong>{t(view.listing ? "Recent side questions" : "Side question")}</strong><span />
      {!view.listing && <Button variant="minimal" size="small" onClick={() => store.list(sessionId)}>{t("Recent side questions")}</Button>}
      <Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Close")} onClick={() => store.close(sessionId)} />
    </header>
    <div className="side-question-body">
      {view.listing ? view.entries.length ? <ul>{[...view.entries].reverse().map(item => <li key={item.id}>
        <button type="button" onClick={() => store.show(sessionId, item.id)}>{item.question}</button>
      </li>)}</ul> : <p>{t("No side questions yet. Type /ask followed by a question in the prompt.")}</p>
      : entry && <><SideQuestionTimeline entry={entry} sessionId={sessionId} />
        {entry.state === "running" && <p role="status">{t("Thinking…")}</p>}
        {entry.state === "stopped" && <p role="status">{t("Question stopped.")}</p>}
        {entry.state === "failed" && <p role="alert">{t("The side question failed ({status}).", { status: entry.failure?.status ?? "failed" })}{entry.failure?.message && <> {entry.failure.message}</>}</p>}
      </>}
    </div>
  </section>;
}
