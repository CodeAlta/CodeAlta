import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { FileChanges } from "./fileChanges";
import { useShellLanguage } from "./shellLanguage";
import { AppIcon } from "./AppIcon";

// Read-only disclosure: paths never become links, filesystem targets or RPC inputs.
// The parent keys this subtree by the exact supplied record, not just byte offset.
export function FileChangeInspection({ changes, canInspect }: { changes: FileChanges; canInspect?: () => boolean }) {
  const { t } = useShellLanguage();
  const id = useId();
  const latest = useRef(canInspect); latest.current = canInspect;
  const [selection, setSelection] = useState<{ index: number; current?: () => boolean } | null>(null);
  const alive = useRef(true);
  const dialog = useRef<HTMLDialogElement>(null);
  const origin = useRef<HTMLButtonElement | null>(null);
  const composing = useRef(false);
  function close() {
    const valid = allowed() && (selection?.current?.() ?? true);
    setSelection(null); dialog.current?.close();
    if (valid && allowed() && (selection?.current?.() ?? true) && origin.current?.isConnected
      && !origin.current.closest('[inert], [hidden]') && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) origin.current.focus();
  }
  const allowed = () => alive.current && (canInspect?.() ?? true) && (latest.current?.() ?? true);
  useLayoutEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useLayoutEffect(() => { if (selection && (!(selection.current?.() ?? true) || !allowed())) setSelection(null); });
  useEffect(() => {
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement && event.target !== dialog.current) setSelection(null); };
    document.addEventListener("beforetoggle", retire, true);
    return () => document.removeEventListener("beforetoggle", retire, true);
  }, []);
  useLayoutEffect(() => {
    const element = dialog.current;
    if (!selection || !element || !allowed()) return;
    element.showModal(); element.querySelector<HTMLButtonElement>('button')?.focus();
    return () => { if (element.open) element.close(); };
  }, [selection]);
  const counted = changes.rows.filter(row => row.counts !== null);
  return <section className="file-change-inspection" aria-label={t("Supplied file records")}>
    <div className="file-change-summary"><span>{changes.rows.length ? t("Supplied file records: {count}", { count: changes.rows.length }) : t("Partial or unsupported file data; inspect the original record details.")}</span>
    <span title={t("Recorded data only, not disk state or write success. Counts cover supplied hunks, not complete file or run totals.")}><AppIcon name="info" size={12} /></span>
    {changes.partial && <span title={t("Partial or unsupported file data; inspect the original record details.")} aria-label={t("Partial or unsupported file data; inspect the original record details.")}><AppIcon name="error" size={12} /></span>}
    {counted.length > 0 && <span>{t("Shown counted hunks ({count} records): +{added} / -{removed}", {
      count: counted.length, added: counted.reduce((sum, row) => sum + row.counts!.added, 0), removed: counted.reduce((sum, row) => sum + row.counts!.removed, 0),
    })}</span>}</div>
    <ul>{changes.rows.map(row => {
      const open = selection?.index === row.index && (selection.current?.() ?? true) && allowed();
      return <li key={row.index}>
        <button type="button" data-file-record={row.index} aria-haspopup="dialog" aria-expanded={open} aria-label={`${t("Inspect file record")} ${row.path}`} disabled={!allowed()}
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => {
            if (event.defaultPrevented || !allowed() || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
            origin.current = event.currentTarget; setSelection(open ? null : { index: row.index, current: canInspect });
          }}><AppIcon name="file" size={14} /><code>{row.path}</code></button>
        <span className="file-change-kind">{row.kind ?? t("Change kind not supplied")}</span>
        <span className="file-counts" title={row.counts ? t("Supplied hunk lines: +{added} / -{removed}", row.counts) : t("Diff counts unavailable")}>{row.counts ? <><b>+{row.counts.added}</b> <em>−{row.counts.removed}</em></> : "—"}</span>
        {open && <dialog ref={dialog} className="app-dialog timeline-details-dialog" aria-labelledby={`${id}-title`} onClose={() => setSelection(null)}
          onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
          onCancel={event => { event.preventDefault(); if (!composing.current) close(); }} onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229 && !event.repeat) close(); } }}>
          <header><h2 id={`${id}-title`}>{row.path}</h2><button type="button" aria-label={t("Close")} onClick={close}><AppIcon name="close" size={18} /></button></header>
          <p>{t("Recorded data only, not disk state or write success. Counts cover supplied hunks, not complete file or run totals.")}</p>
          {row.diff !== null ? <pre data-file-diff>{row.diff}</pre> : <p>{t("No supported per-file diff supplied; original record details remain available.")}</p>}
        </dialog>}
      </li>;
    })}</ul>
  </section>;
}
