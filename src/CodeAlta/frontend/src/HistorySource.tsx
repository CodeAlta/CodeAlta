import { AppWindowSurface } from "./AppWindow";
import { useLayoutEffect, useRef, useState } from "react";
import { workspace, type HistoryRevision, type HistorySourceResponse } from "#neoastra";
import { loadHistorySource } from "./loadHistorySource";
import { historyMessage } from "./history";
import { useShellLanguage } from "./shellLanguage";
import { AppIcon } from "./AppIcon";

export type HistorySourceTarget = { revision: HistoryRevision; start: string; end: string };

// Only one inspector is mounted by History; chunks replace each other, never accumulate.
export function HistorySource({ target, canInspect, onClose }: { target: HistorySourceTarget; canInspect?: () => boolean; onClose: () => void }) {
  const { t } = useShellLanguage();
  const [offset, setOffset] = useState(target.start);
  const [page, setPage] = useState<HistorySourceResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [retired, setRetired] = useState(false);
  const [copied, setCopied] = useState(false);
  const lifetime = useRef(true);
  const root = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  const controller = useRef<AbortController | null>(null);
  const original = useRef(canInspect);
  const latest = useRef(canInspect); latest.current = canInspect;
  const current = () => lifetime.current && (original.current?.() ?? true) && (latest.current?.() ?? true)
    && !!root.current?.isConnected && !root.current.closest("[inert]")
    && !Array.from(document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]')).some(element => element !== root.current);
  function retire() { lifetime.current = false; controller.current?.abort(); setRetired(true); }
  function move(next: string) {
    if (!current() || busy) return;
    controller.current?.abort(); setPage(null); setCopied(false); setBusy(true); setOffset(next);
  }
  useLayoutEffect(() => {
    lifetime.current = true;
    const element = root.current!;
    const origin = document.activeElement;
    if (!(original.current?.() ?? true)) { retire(); return; }
    element.showModal(); closeButton.current?.focus();
    const modal = (event: Event) => { if (event.target instanceof HTMLDialogElement && event.target !== element) retire(); };
    document.addEventListener("beforetoggle", modal, true);
    return () => {
      lifetime.current = false; controller.current?.abort(); document.removeEventListener("beforetoggle", modal, true);
      if (element.open) element.close();
      if (origin instanceof HTMLElement && origin.isConnected && !origin.closest('[inert], [hidden]')
        && (original.current?.() ?? true) && (latest.current?.() ?? true) && !document.querySelector('dialog[open]')) origin.focus();
    };
  }, []);
  useLayoutEffect(() => { if (lifetime.current && !current()) retire(); });
  useLayoutEffect(() => {
    if (!current()) { setRetired(true); return; }
    const abort = new AbortController();
    controller.current = abort;
    setBusy(true); setCopied(false); setPage(null);
    void loadHistorySource(workspace.historySource, { ...target, offset }, abort.signal, current, value => { setPage(value); setBusy(false); });
    return () => abort.abort();
  }, [offset, target]);
  return <dialog ref={root} className="app-dialog history-source" aria-label={t("Full raw journal record")} onClose={onClose}
    onCancel={event => { event.preventDefault(); if (!composing.current) { retire(); onClose(); } }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => {
      event.stopPropagation();
      if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.history-source.v1" title={t("Full raw record · UTF-8 JSON source")} preferredSize={viewport => ({ width: Math.min(900, viewport.width - 40), height: Math.min(640, viewport.height - 40) })}
      onClose={() => { retire(); onClose(); }} closeLabel={t("Close source")} closeRef={closeButton}>
    <p>{t("One chunk at a time, at most 16 KiB. Copy copies only the displayed chunk.")}</p>
    <p>{t("Record bytes")}: {target.start}–{target.end}; {t("Chunk offset")}: {offset}</p>
    {retired && <p role="alert">{t("Source review expired. Close and reopen from the history row.")}</p>}
    {busy && !retired && <p role="status">{t("Loading source chunk…")}</p>}
    {page?.status !== "ok" && page && <p role="alert">{t(historyMessage(page.status))}</p>}
    {!retired && page?.text !== null && page?.text !== undefined && <pre style={{ maxHeight: "24rem", overflow: "auto", whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{page.text}</pre>}
    <button type="button" disabled={busy || retired || offset === target.start} onClick={() => move(target.start)}>{t("First chunk")}</button>
    <button type="button" disabled={busy || retired || !page?.nextOffset} onClick={() => { if (page?.nextOffset) move(page.nextOffset); }}>{t("Next chunk")}</button>
    <button type="button" disabled={busy || retired || page?.status !== "ok"} onClick={() => {
      const read = controller.current;
      if (!current() || busy || !read || read.signal.aborted || page?.text === null || page?.text === undefined) return;
      void navigator.clipboard.writeText(page.text).then(() => {
        if (current() && controller.current === read && !read.signal.aborted) setCopied(true);
      }, () => {});
    }}>{t(copied ? "Chunk copied" : "Copy chunk")}</button>
  </AppWindowSurface></dialog>;
}
