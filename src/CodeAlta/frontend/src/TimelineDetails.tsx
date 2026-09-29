import { useLayoutEffect, useRef } from "react";
import type { TimelineItem } from "./timeline";
import { MarkdownContent } from "./MarkdownContent";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import { CodePreview } from "./CodePreview";
import { isDialogBackdrop } from "./dialogBackdrop";

// Immutable supplied presentation only: opening this dialog grants no RPC or mutation authority.
export function TimelineDetails({ item, current, onClose }: { item: TimelineItem; current: () => boolean; onClose: () => void }) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  useLayoutEffect(() => {
    const element = dialog.current!;
    if (!current()) { onClose(); return; }
    element.showModal(); close.current?.focus();
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement && event.target !== element) onClose(); };
    document.addEventListener("beforetoggle", retire, true);
    return () => { document.removeEventListener("beforetoggle", retire, true); if (element.open) element.close(); };
  }, []);
  useLayoutEffect(() => { if (!current()) onClose(); });
  return <dialog ref={dialog} className="app-dialog timeline-details-dialog" aria-label={`${t("Details")} · ${item.title}`}
    onClick={event => { if (!composing.current && isDialogBackdrop(event)) onClose(); }}
    // Effect replay can reopen the element before cleanup's queued close event arrives.
    onClose={event => { if (!event.currentTarget.open) onClose(); }} onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!event.repeat && !composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) onClose(); } }}>
    <header><h2>{item.title}</h2><button ref={close} type="button" aria-label={t("Close")} title={t("Close")} onClick={onClose}><AppIcon name="close" size={18} /></button></header>
    <div className="dialog-panes">
    {item.summary && <section className="detail-pane"><CodePreview text={item.summary} /></section>}
    {item.markdown && item.category !== "file" && <section className="detail-pane"><MarkdownContent source={item.markdown} timelineCodeBlocks /></section>}
    {item.detailMarkdown && item.category !== "file" && item.detailMarkdown !== item.markdown && <section className="detail-pane"><MarkdownContent source={item.detailMarkdown} timelineCodeBlocks /></section>}
    {(item.toolFields?.length ? item.toolFields : item.toolRecord?.fields)?.map(field => <section className="detail-pane" key={field.path}><h3>{field.path}</h3><CodePreview text={field.text} field={field.path} /></section>)}
    {item.toolOutput && ![...(item.toolFields ?? []), ...(item.toolRecord?.fields ?? [])].some(field => field.text.includes(item.toolOutput!)) && <section className="detail-pane"><CodePreview text={item.toolOutput} /></section>}
    {item.details && !["tool", "file"].includes(item.category) && <section className="detail-pane"><pre>{item.details}</pre></section>}
    </div>
  </dialog>;
}
