import { AppWindowSurface } from "./AppWindow";
import { useLayoutEffect, useRef } from "react";
import type { TimelineItem } from "./timeline";
import { MarkdownContent } from "./MarkdownContent";
import { PluginHtml } from "./PluginHtml";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import { CodePreview } from "./CodePreview";
import { DiffPreview } from "./changes/DiffPreview";
import { isDialogBackdrop } from "./dialogBackdrop";

// Immutable supplied presentation only: opening this dialog grants no RPC or mutation authority. The links of
// its texts are followed by the opener of the window, as those of a message are.
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
    <AppWindowSurface storageKey="codealta.desktop.window.timeline-details.v1" title={item.title} preferredSize={viewport => ({ width: Math.min(900, viewport.width - 40), height: Math.min(640, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={close}>
    <div className="dialog-panes">
    {item.html && <section className="detail-pane"><PluginHtml html={item.html} pluginKey={item.pluginKey} script={{ path: item.script ?? null, problem: item.scriptProblem ?? null }} /></section>}
    {item.summary && !item.html && <section className="detail-pane"><CodePreview text={item.summary} /></section>}
    {item.markdown && item.category !== "file" && <section className="detail-pane"><MarkdownContent source={item.markdown} timelineCodeBlocks /></section>}
    {item.detailMarkdown && item.category !== "file" && item.detailMarkdown !== item.markdown && <section className="detail-pane"><MarkdownContent source={item.detailMarkdown} timelineCodeBlocks /></section>}
    {item.detailSections?.map((detail, index) => <section className="detail-pane plugin-detail" key={index}><h3>{detail.header}</h3>
      {detail.html ? <PluginHtml html={detail.html} pluginKey={item.pluginKey} /> : <MarkdownContent source={detail.markdown ?? ""} timelineCodeBlocks />}</section>)}
    {item.toolFields?.map(field => field.path === "diff"
      // The diff an edit left behind comes first, as a diff.
      ? <section className="detail-pane detail-pane-diff" key={field.path}><h3>{t("Changes")}{item.toolChanges
        && <span className="file-counts"><b>+{item.toolChanges.added}</b> <em>−{item.toolChanges.removed}</em></span>}</h3><DiffPreview text={field.text} /></section>
      : <section className="detail-pane" key={field.path}><h3>{field.path}</h3><CodePreview text={field.text} field={field.path} /></section>)}
    {item.toolOutput && !(item.toolFields ?? []).some(field => field.text.includes(item.toolOutput!)) && <section className="detail-pane"><CodePreview text={item.toolOutput} /></section>}
    {item.details && !["tool", "file"].includes(item.category) && <section className="detail-pane"><pre>{item.details}</pre></section>}
    </div>
  </AppWindowSurface></dialog>;
}
