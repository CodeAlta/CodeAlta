import { useLayoutEffect, useRef } from "react";
import type { TimelineItem } from "./timeline";
import { MarkdownContent } from "./MarkdownContent";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

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
    onClose={onClose} onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!event.repeat && !composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) onClose(); } }}>
    <header><h2>{item.title}</h2><button ref={close} type="button" aria-label={t("Close")} title={t("Close")} onClick={onClose}><AppIcon name="close" size={18} /></button></header>
    {item.summary && <pre>{item.summary}</pre>}
    {item.markdown && <MarkdownContent source={item.markdown} timelineCodeBlocks />}
    {item.detailMarkdown && item.detailMarkdown !== item.markdown && <MarkdownContent source={item.detailMarkdown} timelineCodeBlocks />}
    {item.details && <pre>{item.details}</pre>}
    <ul>{item.metadata.map(value => <li key={value}>{value}</li>)}</ul>
    {item.bodyOmitted && <p>{t("Additional diagnostic details were omitted.")}</p>}
    {item.truncated && <p>{t("Some details were shortened to fit the desktop history window.")}</p>}
  </dialog>;
}
