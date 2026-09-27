import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { FileChanges } from "./fileChanges";
import { useShellLanguage } from "./shellLanguage";

// Read-only disclosure: paths never become links, filesystem targets or RPC inputs.
// The parent keys this subtree by the exact supplied record, not just byte offset.
export function FileChangeInspection({ changes, canInspect }: { changes: FileChanges; canInspect?: () => boolean }) {
  const { t } = useShellLanguage();
  const id = useId();
  const latest = useRef(canInspect); latest.current = canInspect;
  const [selection, setSelection] = useState<{ index: number; current?: () => boolean } | null>(null);
  const alive = useRef(true);
  const allowed = () => alive.current && (canInspect?.() ?? true) && (latest.current?.() ?? true);
  useLayoutEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useLayoutEffect(() => { if (selection && (!(selection.current?.() ?? true) || !allowed())) setSelection(null); });
  useEffect(() => {
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement) setSelection(null); };
    document.addEventListener("beforetoggle", retire, true);
    return () => document.removeEventListener("beforetoggle", retire, true);
  }, []);
  const counted = changes.rows.filter(row => row.counts !== null);
  return <section className="file-change-inspection" aria-label={t("Supplied file records")}>
    <p>{t("Supplied file records: {count}", { count: changes.rows.length })}</p>
    <p className="muted-text">{t("Recorded data only, not disk state or write success. Counts cover supplied hunks, not complete file or run totals.")}</p>
    {changes.partial && <p role="status">{t("Partial or unsupported file data; inspect the original record details.")}</p>}
    {counted.length > 0 && <p>{t("Shown counted hunks ({count} records): +{added} / -{removed}", {
      count: counted.length, added: counted.reduce((sum, row) => sum + row.counts!.added, 0), removed: counted.reduce((sum, row) => sum + row.counts!.removed, 0),
    })}</p>}
    <ul>{changes.rows.map(row => {
      const open = selection?.index === row.index && (selection.current?.() ?? true) && allowed();
      return <li key={row.index}>
        <button type="button" data-file-record={row.index} aria-expanded={open} aria-controls={`${id}-${row.index}`} disabled={!allowed()}
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => {
            if (event.defaultPrevented || !allowed() || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
            setSelection(open ? null : { index: row.index, current: canInspect });
          }}><span>{t("Inspect file record")}</span> <code>{row.path}</code></button>
        <span className="file-change-kind">{row.kind ?? t("Change kind not supplied")}</span>
        <p>{row.counts ? t("Supplied hunk lines: +{added} / -{removed}", row.counts) : t("Diff counts unavailable")}</p>
        <div id={`${id}-${row.index}`} hidden={!open}>
          {open && (row.diff !== null ? <pre data-file-diff>{row.diff}</pre> : <p>{t("No supported per-file diff supplied; original record details remain available.")}</p>)}
        </div>
      </li>;
    })}</ul>
  </section>;
}
