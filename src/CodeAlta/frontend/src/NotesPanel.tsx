import { useEffect, useRef, useState } from "react";
import { MarkdownContent } from "./MarkdownContent";
import { notesMessage, type createNotesReader, type NotesState } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";

export function NotesPanel({ epoch, sessionId, reader, capability, fallbackMarkdown, onClose }: {
  epoch?: string;
  sessionId: string | null;
  reader?: ReturnType<typeof createNotesReader>;
  capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string;
  onClose: () => void;
}) {
  const [state, setState] = useState<NotesState>();
  const selection = useRef<ReturnType<ReturnType<typeof createNotesReader>["forSelection"]> | undefined>(undefined);
  useEffect(() => {
    setState(undefined);
    if (!epoch || !sessionId || !reader || !capability) return;
    const controller = new AbortController();
    selection.current = reader.forSelection(epoch, sessionId, controller.signal, setState,
      () => { capability.observe({ status: "stale_epoch", epoch }); });
    void selection.current.refresh();
    return () => { controller.abort(); selection.current = undefined; };
  }, [epoch, sessionId, reader, capability]);

  const markdown = state?.kind === "ready" ? state.markdown : fallbackMarkdown;
  return <section className="notes-pane" aria-label="Alta notes" tabIndex={-1}>
    <header><span><strong>Alta notes</strong><small>Markdown · session scoped</small></span><span>
      {selection.current && <button type="button" title="Refresh notes" aria-label="Refresh notes" onClick={() => void selection.current?.refresh()}>↻</button>}
      <button type="button" title="Hide notes" aria-label="Hide notes" onClick={onClose}>×</button>
    </span></header>
    <div className="notes-content">
      {!sessionId && <p className="muted-text">Select a session to view its notes.</p>}
      {state?.kind === "loading" && !markdown && <p role="status" className="muted-text">Reading notes…</p>}
      {state?.kind === "error" && <p role="alert" className="error-text">{notesMessage(state.code)}</p>}
      {sessionId && !markdown && state?.kind !== "loading" && <p className="muted-text">No notes for this session.</p>}
      {markdown && <MarkdownContent source={markdown} />}
    </div>
  </section>;
}
