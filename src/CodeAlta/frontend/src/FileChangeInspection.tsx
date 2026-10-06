import { AppWindowSurface } from "./AppWindow";
import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { FileChanges } from "./fileChanges";
import { useShellLanguage } from "./shellLanguage";
import { AppIcon } from "./AppIcon";
import { isDialogBackdrop } from "./dialogBackdrop";
import { DiffPreview } from "./changes/DiffPreview";

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
  return <section className="file-change-inspection" aria-label={t("Supplied file records")}>
    <ul>{changes.rows.map(row => {
      const open = selection?.index === row.index && (selection.current?.() ?? true) && allowed();
      return <li key={row.index}>
        <button type="button" data-file-record={row.index} aria-haspopup="dialog" aria-expanded={open} aria-label={`${t("Inspect file record")} ${row.path}`} disabled={!allowed()}
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => {
            if (event.defaultPrevented || !allowed() || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
            origin.current = event.currentTarget; setSelection(open ? null : { index: row.index, current: canInspect });
          }} title={row.path}><span className="file-card-path"><strong>{row.path.split(/[\\/]/).at(-1)}</strong><small>{row.path.replace(/[\\/][^\\/]+$/, "") === row.path ? "." : row.path.replace(/[\\/][^\\/]+$/, "")}</small></span></button>
        <span className="file-counts" title={row.counts ? t("Supplied hunk lines: +{added} / -{removed}", row.counts) : t("Diff counts unavailable")}>{row.counts ? <><b>+{row.counts.added}</b> <em>−{row.counts.removed}</em></> : "—"}</span>
        {open && <dialog ref={dialog} className="app-dialog timeline-details-dialog" aria-labelledby={`${id}-title`} onClose={event => { if (!event.currentTarget.open) setSelection(null); }}
          onClick={event => { if (!composing.current && isDialogBackdrop(event)) close(); }}
          onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
          onCancel={event => { event.preventDefault(); if (!composing.current) close(); }} onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229 && !event.repeat) close(); } }}>
          <AppWindowSurface storageKey="codealta.desktop.window.file-change.v1" title={row.path} titleId={`${id}-title`} preferredSize={viewport => ({ width: Math.min(980, viewport.width - 40), height: Math.min(700, viewport.height - 40) })}
            onClose={close} closeLabel={t("Close")}>
          <div className="file-dialog-summary"><span>{row.kind}</span>{row.counts && <span className="file-counts"><b>+{row.counts.added}</b> <em>-{row.counts.removed}</em></span>}</div>
          {row.diff !== null ? <DiffPreview text={row.diff} /> : <p>{t("No supported per-file diff supplied; original record details remain available.")}</p>}
        </AppWindowSurface></dialog>}
      </li>;
    })}</ul>
  </section>;
}
