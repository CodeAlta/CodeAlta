import { useLayoutEffect, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { copySessionId, dismissSessionInfoOnKey, sessionInfoCopyFeedback, type SessionInfoView } from "./sessionInfo";

export function SessionInfoDialog({ info, demo, onClose }: {
  info: SessionInfoView; demo: boolean; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const alive = useRef(false);
  const copying = useRef(false);
  const composingEscape = useRef(false);
  const closing = useRef(false);
  const [feedback, setFeedback] = useState<"copied" | "unavailable" | "failed" | null>(null);
  useLayoutEffect(() => {
    const element = dialog.current;
    alive.current = true;
    element?.showModal();
    return () => { alive.current = false; if (element?.open) element.close(); };
  }, []);
  function close() {
    if (closing.current) return;
    closing.current = true;
    onClose();
  }

  async function copy() {
    if (!info.canCopyId || copying.current) return;
    copying.current = true;
    setFeedback(null);
    const result = await copySessionId(info.id,
      () => navigator.clipboard ? text => navigator.clipboard.writeText(text) : undefined);
    copying.current = false;
    if (alive.current) setFeedback(result);
  }

  return <dialog ref={dialog} className="app-dialog session-info-dialog" aria-modal="true" aria-labelledby="session-info-title" aria-describedby="session-info-description"
    onKeyDown={event => {
      event.stopPropagation(); // Do not run shell chords while this native modal is open.
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (dismissSessionInfoOnKey({ key: event.key, isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode })) close();
      else composingEscape.current = true;
    }}
    onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
    <header><div><span className="eyebrow">Selected session</span><h2 id="session-info-title">Session info</h2></div>
      <button autoFocus type="button" className="icon-button" aria-label="Close session info" onClick={close}><AppIcon name="close" size={16} /></button></header>
    <p id="session-info-description" className="muted-text">{demo ? "Demo snapshot; values are local to this preview." : "Saved catalog metadata, not live runtime status."}</p>
    <dl className="session-info-fields" tabIndex={0} aria-label="Recorded session information">
      <div><dt>Session ID</dt><dd><code>{info.id || "Not recorded"}</code></dd></div>
      <div><dt>Title</dt><dd>{info.title}{info.titleTruncated && <small>Title shortened in the bounded snapshot.</small>}</dd></div>
      <div><dt>Scope</dt><dd>{info.scope}{info.scopeWarning && <small>{info.scopeWarning}</small>}</dd></div>
      <div><dt>Recorded working directory</dt><dd>{info.path ?? "Not recorded or unverified"}</dd></div>
      <div><dt>Provider</dt><dd>{info.provider ?? "Not recorded or unverified"}</dd></div>
      <div><dt>Saved update</dt><dd>{info.updatedAt ? <time dateTime={info.updatedAt}>{info.updatedAt}</time> : "Not recorded or unverified"}</dd></div>
    </dl>
    <footer><span>{feedback && <span role={feedback === "copied" ? "status" : "alert"}>
      {sessionInfoCopyFeedback(feedback)}
    </span>}</span><span><button type="button" className="quiet-button" disabled={!info.canCopyId} onClick={() => void copy()}>Copy session ID</button>{" "}
      <button type="button" className="quiet-button" onClick={close}>Close</button></span></footer>
  </dialog>;
}
