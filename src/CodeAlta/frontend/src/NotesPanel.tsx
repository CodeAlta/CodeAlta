import { useEffect, useRef, useState } from "react";
import { notesMessage, type createNotesReader, type NotesState } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";

export function NotesPanel({ epoch, sessionId, reader, capability }: {
  epoch: string; sessionId: string; reader: ReturnType<typeof createNotesReader>; capability: ReturnType<typeof createMutationCapability>;
}) {
  const [state, setState] = useState<NotesState>();
  const selection = useRef<ReturnType<typeof reader.forSelection> | undefined>(undefined);
  useEffect(() => {
    const controller = new AbortController();
    selection.current = reader.forSelection(epoch, sessionId, controller.signal, setState,
      () => { capability.observe({ status: "stale_epoch", epoch }); });
    return () => { controller.abort(); selection.current = undefined; };
  }, [epoch, sessionId, reader, capability]);
  return <section aria-label="Current durable notes">
    <h3>Current durable notes — read only</h3>
    <p className="detail">Refresh reads the latest stored notes, not live progress. Complete literal text only, up to 16,384 UTF-16 units. The underlying journal scan is not bounded by this display limit.</p>
    <button type="button" onClick={() => { void selection.current?.refresh(); }}>Refresh notes</button>
    {!state && <p role="status">Notes have not been read. Use Refresh notes.</p>}
    {state?.kind === "loading" && <p role="status">Reading notes…</p>}
    {state?.kind === "error" && <p role="alert">{notesMessage(state.code)}</p>}
    {state?.kind === "ready" && (state.markdown === "" ? <p role="status">No stored notes text.</p> : <pre>{state.markdown}</pre>)}
  </section>;
}
