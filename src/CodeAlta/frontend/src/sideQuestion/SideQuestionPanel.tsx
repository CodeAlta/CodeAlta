import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type PointerEvent } from "react";
import { Button, InputGroup, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { modalDialogOpen } from "../modalDialogs";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { TimelineGroupView } from "../TimelineEntryGroup";
import { emphasisProperties } from "../timelineEmphasis/timelineEmphasis";
import { useTimelineEmphasis } from "../timelineEmphasis/useTimelineEmphasis";
import type { TimelineGroup } from "../toolGroups";
import type { MessageKey } from "../localization";
import { answerPreview, type SideQuestionEntry, type SideQuestions } from "./sideQuestions";
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

// What a failed answer says, by the status of the host; what the host said goes after it only when nothing more precise does.
const failures: Readonly<Record<string, MessageKey>> = {
  unsupported: "The provider of this session runs an agent of its own and cannot answer side questions.",
  missing_session: "This session can no longer be read.",
  stale_epoch: "CodeAlta restarted since this question was asked: ask it again.",
  closed: "CodeAlta restarted since this question was asked: ask it again.",
  invalid_request: "This question cannot be asked.",
};

/**
 * A transient answer over this session's timeline; the composer and running turn remain in place. It fits what it shows,
 * up to half the timeline, until its edge is dragged; a field at its foot asks another question, and its list shows the
 * questions asked of the session, the latest first, with how long ago and how each ended.
 */
export function SideQuestionPanel({ store, sessionId, active }: { store: SideQuestions; sessionId: string; active: boolean }) {
  const { t, locale } = useShellLanguage();
  // Null: the panel fits what it shows; a drag gives it a height of its own, in percent of the timeline, until a reset.
  const [height, setHeight] = useState<number | null>(null);
  const [draft, setDraft] = useState("");
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
  function ask() {
    const question = draft.trim();
    if (!question) return;
    store.ask(sessionId, question);
    setDraft("");
  }
  const entry = view.entries.find(item => item.id === view.shown);
  const count = view.entries.length;
  return <section ref={panel} className="side-question" aria-label={t("Side question")} data-fit={height === null || undefined}
    style={height === null ? undefined : { height: `${height}%` }}>
    <div ref={grip} className="side-question-grip" role="separator" aria-label={t("Resize side question")} aria-orientation="horizontal"
      aria-valuemin={0} aria-valuemax={100} aria-valuenow={height === null ? undefined : Math.round(height)} tabIndex={0}
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
      onDoubleClick={() => setHeight(null)} onKeyDown={event => {
        if (event.key !== "ArrowUp" && event.key !== "ArrowDown" && event.key !== "Home") return;
        if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
        event.preventDefault(); event.stopPropagation();
        if (event.key === "Home") setHeight(null);
        else resize((panel.current?.getBoundingClientRect().height ?? 0) + (event.key === "ArrowUp" ? 16 : -16));
      }}><span /></div>
    <header>
      <AppIcon name="chat" size={15} className="side-question-icon" />
      <strong>{t(view.listing ? "Recent side questions" : "Side question")}</strong>
      {view.listing && count > 0 && <Tag minimal round>{count}</Tag>}
      <span className="side-question-hint">{t("Not added to the conversation")}</span>
      {!view.listing && count > 1 && <Button className="side-question-recent" variant="minimal" size="small" icon={<AppIcon name="history" size={14} />}
        title={t("Recent side questions")} onClick={() => store.list(sessionId)}>{t("Recent")}<Tag minimal round>{count}</Tag></Button>}
      <Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Close")} title={t("Close (Esc)")} onClick={() => store.close(sessionId)} />
    </header>
    <div className="side-question-body">
      {view.listing ? count ? <ul className="side-question-list">{[...view.entries].reverse().map(item => {
        const asked = sessionTime(item.askedAt, locale);
        const preview = answerPreview(item.answer);
        return <li key={item.id} data-state={item.state}><button type="button" onClick={() => store.show(sessionId, item.id)}>
          <span className="side-question-row-head"><strong>{item.question}</strong><time dateTime={asked.dateTime} title={asked.title}>{asked.label}</time></span>
          <span className="side-question-row-answer">
            {item.state === "running" ? <><ActivitySpinner size={12} />{t("Thinking…")}</>
              : item.state === "failed" ? <><AppIcon name="warning" size={12} />{t("Failed")}</>
              : item.state === "stopped" && !preview ? t("Stopped")
              : preview}
          </span>
        </button></li>;
      })}</ul>
        : <div className="side-question-empty"><AppIcon name="chat" size={26} /><strong>{t("No side questions yet.")}</strong>
          <span>{t("Ask one below: the session goes on, and nothing is added to its conversation.")}</span></div>
      : entry && <><SideQuestionTimeline entry={entry} sessionId={sessionId} />
        {/* Until the first words: then the card of the answer says that it streams. */}
        {entry.state === "running" && !entry.answer && <p role="status" className="side-question-status"><ActivitySpinner size={14} />{t("Thinking…")}</p>}
        {entry.state === "stopped" && <p role="status" className="side-question-status">{t("Question stopped.")}</p>}
        {entry.state === "failed" && <p role="alert" className="side-question-status side-question-failure"><AppIcon name="warning" size={14} />
          <span>{entry.failure && failures[entry.failure.status] ? t(failures[entry.failure.status])
            : <>{t("The side question failed.")}{entry.failure?.message && <> {entry.failure.message}</>}</>}</span></p>}
      </>}
    </div>
    {/* Another question, without going back to the prompt: the panel stays, and the answer replaces the one shown. */}
    <form className="side-question-ask" onSubmit={event => { event.preventDefault(); ask(); }}>
      <InputGroup size="small" value={draft} onValueChange={setDraft} maxLength={16_384} aria-label={t(count ? "Ask another side question…" : "Ask a side question…")}
        placeholder={t(count ? "Ask another side question…" : "Ask a side question…")} leftIcon={<AppIcon name="chat" size={14} className="bp6-icon" />} />
      <Button type="submit" size="small" intent="primary" icon={<AppIcon name="send" size={14} />} disabled={!draft.trim()} aria-label={t("Ask")} title={t("Ask")} />
    </form>
  </section>;
}
