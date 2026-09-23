import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { MarkdownContent } from "./MarkdownContent";
import { canClearNotes, copyNotesMarkdown, notesMessage, type createNotesReader, type NotesClearState, type NotesState } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";
import { notesResizeKey, visibleNotesHeight } from "./notesHeight";
import { AppIcon } from "./AppIcon";

export function NotesPanel({ epoch, sessionId, reader, capability, fallbackMarkdown, preferredHeight, onResize, onReset, onCleared, onClose }: {
  epoch?: string;
  sessionId: string | null;
  reader?: ReturnType<typeof createNotesReader>;
  capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string;
  preferredHeight: number;
  onResize: (delta: number) => void;
  onReset: () => void;
  onCleared: (sessionId: string) => void;
  onClose: () => void;
}) {
  const [observed, setObserved] = useState<{ epoch: string; sessionId: string; state: NotesState }>();
  const [action, setAction] = useState<{ epoch: string; sessionId: string; state: NotesClearState }>();
  const [copyStatus, setCopyStatus] = useState<{ epoch: string; sessionId: string; kind: "copied" | "copy_failed" }>();
  const [railHeight, setRailHeight] = useState(0);
  const [renderedHeight, setRenderedHeight] = useState(0);
  const pane = useRef<HTMLElement>(null);
  const selection = useRef<{ epoch: string; sessionId: string; actions: ReturnType<ReturnType<typeof createNotesReader>["forSelection"]> } | undefined>(undefined);
  const lastY = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (!epoch || !sessionId || !reader || !capability) { selection.current = undefined; return; }
    setAction(undefined);
    setCopyStatus(undefined);
    setObserved({ epoch, sessionId, state: { kind: "loading" } });
    const controller = new AbortController();
    const actions = reader.forSelection(epoch, sessionId, controller.signal,
      state => setObserved({ epoch, sessionId, state }),
      () => { capability.observe({ status: "stale_epoch", epoch }); });
    selection.current = { epoch, sessionId, actions };
    const uncertain = actions.uncertainClear();
    if (uncertain) setAction({ epoch, sessionId, state: uncertain });
    // StrictMode may dispose the first effect immediately. Do not admit its obsolete read.
    queueMicrotask(() => {
      if (controller.signal.aborted) return;
      void actions.refresh().then(() => {
        const uncertainAfterRead = actions.uncertainClear();
        if (!controller.signal.aborted && uncertainAfterRead)
          setAction({ epoch, sessionId, state: uncertainAfterRead });
      });
    });
    return () => { controller.abort(); selection.current = undefined; };
  }, [epoch, sessionId, reader, capability]);
  useEffect(() => {
    const rail = pane.current?.parentElement;
    if (!rail) return;
    const observer = new ResizeObserver(() => setRailHeight(rail.clientHeight));
    const paneObserver = new ResizeObserver(() => setRenderedHeight(pane.current?.clientHeight ?? 0));
    observer.observe(rail);
    if (pane.current) paneObserver.observe(pane.current);
    setRailHeight(rail.clientHeight);
    return () => { observer.disconnect(); paneObserver.disconnect(); };
  }, []);

  const state = observed && observed.epoch === epoch && observed.sessionId === sessionId ? observed.state : undefined;
  const result = action && action.epoch === epoch && action.sessionId === sessionId ? action.state : undefined;
  const copied = copyStatus && copyStatus.epoch === epoch && copyStatus.sessionId === sessionId ? copyStatus.kind : undefined;
  const selected = selection.current;
  const current = selected && selected.epoch === epoch && selected.sessionId === sessionId ? selected.actions : undefined;
  const clearEnabled = !!current && canClearNotes(state, current.uncertainClear(), !!capability?.canMutate(), result);
  const markdown = state?.kind === "ready" ? state.markdown : fallbackMarkdown;
  const height = visibleNotesHeight(preferredHeight, railHeight);
  const showAction = (value: NotesClearState) => {
    if (epoch && sessionId) setAction({ epoch, sessionId, state: value });
  };
  function pointerEnd(event: PointerEvent<HTMLDivElement>) {
    lastY.current = undefined;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    const change = notesResizeKey(event.key);
    if (change === null) return;
    event.preventDefault();
    if (change === "reset") onReset();
    else onResize(change);
  }
  return <>
    <div className="notes-splitter" role="separator" aria-label="Resize Alta notes" aria-orientation="horizontal"
      aria-valuemin={112} aria-valuemax={Math.max(112, Math.floor(railHeight * 0.65))} aria-valuenow={renderedHeight || height}
      tabIndex={0} onKeyDown={keyDown} onDoubleClick={onReset}
      onPointerDown={event => { lastY.current = event.clientY; event.currentTarget.setPointerCapture(event.pointerId); }}
      onPointerMove={event => {
        if (lastY.current === undefined || !event.currentTarget.hasPointerCapture(event.pointerId)) return;
        const delta = event.clientY - lastY.current; lastY.current = event.clientY; onResize(delta);
      }} onPointerUp={pointerEnd} onPointerCancel={pointerEnd}><span /></div>
    <section ref={pane} className="notes-pane" aria-label="Alta notes" tabIndex={-1} style={{ height }}>
      <header><span><strong>Alta notes</strong><small>Markdown · session scoped</small></span><span>
        {current && <button type="button" title="Refresh notes" aria-label="Refresh notes" disabled={result?.kind === "clearing"}
          onClick={() => void current.reconcile().then(() => {
            if (selection.current?.actions === current && !current.uncertainClear())
              setAction(previous => previous && previous.epoch === epoch && previous.sessionId === sessionId
                && previous.state.kind === "error" && previous.state.code === "clear_unconfirmed" ? undefined : previous);
          })}><AppIcon name="refresh" size={14} /></button>}
        <button type="button" title="Copy notes as Markdown" aria-label="Copy notes as Markdown"
          disabled={state?.kind !== "ready" || !state.markdown || result?.kind === "clearing"}
          onClick={() => { if (state?.kind === "ready" && epoch && sessionId) void copyNotesMarkdown(state.markdown, text => navigator.clipboard.writeText(text))
            .then(kind => { if (selection.current === selected) setCopyStatus({ epoch, sessionId, kind }); }); }}>Copy</button>
        <button type="button" title="Clear notes" aria-label="Clear notes" disabled={!clearEnabled}
          onClick={() => { if (clearEnabled && current && sessionId) void current.clear(showAction, () => onCleared(sessionId)); }}>Clear</button>
        <button type="button" title="Hide notes" aria-label="Hide notes" onClick={onClose}><AppIcon name="close" size={14} /></button>
      </span></header>
      <div className="notes-content">
        {!sessionId && <p className="muted-text">Select a session to view its notes.</p>}
        {state?.kind === "loading" && !markdown && <p role="status" className="muted-text">Reading notes…</p>}
        {state?.kind === "error" && <p role="alert" className="error-text">{notesMessage(state.code)}</p>}
        {copied === "copied" && <p role="status">Markdown copied.</p>}
        {copied === "copy_failed" && <p role="alert" className="error-text">Clipboard write failed. Notes were not changed.</p>}
        {result?.kind === "clearing" && <p role="status">Clearing notes…</p>}
        {result?.kind === "error" && <p role="alert" className="error-text">{result.code === "stale_epoch" ? "Host changed. Reload before further operations." :
          result.code === "clear_unconfirmed" ? "Clear outcome is uncertain. Refresh and confirm notes still exist before another Clear." :
            "Notes could not be cleared. Read this session's notes again before retrying."}</p>}
        {result?.kind === "cleared" && <p role="status">Notes cleared.</p>}
        {sessionId && !markdown && state?.kind !== "loading" && <p className="muted-text">No notes for this session.</p>}
        {markdown && <MarkdownContent source={markdown} />}
      </div>
    </section>
  </>;
}
