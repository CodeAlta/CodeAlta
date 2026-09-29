import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { ToolRecord } from "./toolRecords";
import { createPaletteFocusRestoration } from "./paletteActions";
import { useShellLanguage } from "./shellLanguage";
import { AppIcon } from "./AppIcon";
import { CodePreview } from "./CodePreview";
import { isDialogBackdrop } from "./dialogBackdrop";

// Parent keys this component by the complete bounded supplied record, not activity ID.
export function ToolRecordInspection({ record, canInspect }: { record: ToolRecord; canInspect?: () => boolean }) {
  const { t } = useShellLanguage();
  const id = useId();
  const latest = useRef(canInspect); latest.current = canInspect;
  const alive = useRef(true), composing = useRef(false);
  const dialog = useRef<HTMLDialogElement>(null), closeButton = useRef<HTMLButtonElement>(null);
  type Review = { origin: HTMLButtonElement; current?: () => boolean; valid: boolean };
  const [review, setReview] = useState<Review | null>(null);
  const active = useRef<Review | null>(null);
  const [copied, setCopied] = useState(false);
  const [retired, setRetired] = useState(false);
  const [focus] = useState(createPaletteFocusRestoration);
  const allowed = () => alive.current && (canInspect?.() ?? true) && (latest.current?.() ?? true);
  const current = (value: Review) => active.current === value && value.valid && allowed()
    && (value.current?.() ?? true) && !!dialog.current?.open && dialog.current.isConnected && !dialog.current.closest("[inert]");
  function close() {
    const value = active.current;
    active.current = null; setReview(null); dialog.current?.close();
    if (value) focus.schedule(value.origin, () => allowed() && (value.current?.() ?? true)
      && value.origin.isConnected && !value.origin.closest("[inert]"),
    () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }
  useLayoutEffect(() => { alive.current = true; return () => { alive.current = false; active.current = null; focus.cancel(); }; }, [focus]);
  useEffect(() => {
    const retire = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement) || !active.current) return;
      if (event.target === dialog.current && (event as ToggleEvent).newState === "open" && !dialog.current.open) return;
      active.current.valid = false; setCopied(false); setRetired(true);
    };
    document.addEventListener("beforetoggle", retire, true);
    return () => document.removeEventListener("beforetoggle", retire, true);
  }, []);
  useLayoutEffect(() => {
    if (!review) return;
    const element = dialog.current!; element.showModal(); closeButton.current?.focus();
    return () => { if (element.open) element.close(); };
  }, [review]);
  useLayoutEffect(() => { if (review && (!allowed() || !(review.current?.() ?? true))) close(); });
  return <>
    <button type="button" className="tool-record-trigger" aria-label={t("Inspect supplied tool record")} title={t("Inspect supplied tool record")} aria-haspopup="dialog" aria-expanded={!!review} disabled={!allowed()}
      onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
      onClick={event => {
        if (event.defaultPrevented || !allowed() || active.current || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
          || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
        focus.cancel(); composing.current = false; setCopied(false); setRetired(false);
        const value = { origin: event.currentTarget, current: canInspect, valid: true };
        active.current = value; setReview(value);
      }}><AppIcon name="tool" size={14} /></button>
    {review && <dialog ref={dialog} className="app-dialog tool-record-dialog" aria-modal="true" aria-labelledby={id}
      onClick={event => { if (!composing.current && isDialogBackdrop(event)) close(); }}
      onClose={event => { if (!event.currentTarget.open && active.current === review) close(); }} onCancel={event => { event.preventDefault(); if (!composing.current) close(); }}
      onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.defaultPrevented) return;
        if (composing.current || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) {
          if (event.key === "Escape" || event.key === "Enter") event.preventDefault(); return;
        }
        if (event.key === "Escape") { event.preventDefault(); close(); }
      }}>
      <header><h2 id={id}>{t("Inspect supplied tool record")}</h2><button ref={closeButton} type="button" aria-label={t("Close")} onClick={() => { if (active.current === review) close(); }}><AppIcon name="close" size={18} /></button></header>
      <p><code>{record.name ?? t("Unknown")}</code></p>
      <div className="dialog-panes">{record.fields.map(field => <section className="detail-pane" key={field.path}><h3><code>{field.path}</code></h3>
        <CodePreview field={field.path} text={field.text} />
      </section>)}</div>
      <button type="button" className="tool-record-copy" disabled={retired || !allowed()} onClick={async event => {
        if (event.defaultPrevented || !event.currentTarget.isConnected || !current(review)) return;
        try { await navigator.clipboard.writeText(record.raw); if (current(review)) setCopied(true); }
        catch { /* No retry or detached feedback. Original raw Copy remains available. */ }
      }}>{t("Copy supplied tool JSON")}</button>
      {copied && <p role="status">{t("Copied")}</p>}
    </dialog>}
  </>;
}
