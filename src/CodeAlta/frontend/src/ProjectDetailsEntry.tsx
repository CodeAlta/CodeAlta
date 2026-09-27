import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { savedProjectSelection } from "./savedProjectSelection";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

export type ProjectDetailsContext = Readonly<{ snapshot: WorkspaceSnapshot | undefined; projectId: string | null;
  sessionId: string | null; hostEpoch: string | null; hostAvailable: boolean;
  refreshVersion: number; refreshReady: boolean; active: boolean }>;

function sameContext(a: ProjectDetailsContext, b: ProjectDetailsContext): boolean {
  return a.active && b.active && a.refreshReady && b.refreshReady && a.snapshot === b.snapshot
    && a.projectId === b.projectId && a.sessionId === b.sessionId && a.hostEpoch === b.hostEpoch
    && a.hostAvailable === b.hostAvailable && a.refreshVersion === b.refreshVersion;
}

export function selectedProjectDetails(context: ProjectDetailsContext): WorkspaceProject | null {
  if (!context.active || !context.refreshReady || !context.snapshot || context.projectId === null) return null;
  const matches = context.snapshot.projects.filter(project => project.id === context.projectId);
  if (matches.length !== 1) return null;
  const project = matches[0];
  return typeof project.id === "string" && project.id.trim().length > 0 && project.id.length <= 256
    && typeof project.path === "string" && project.path.trim().length > 0 && project.path.length <= 4096
    && typeof project.name === "string" && project.name.trim().length > 0 && project.name.length <= 256
    && typeof project.archived === "boolean" && savedProjectSelection(project, context.snapshot) ? project : null;
}

async function copyProjectValue(text: string): Promise<"copied" | "unavailable" | "failed"> {
  try {
    if (!navigator.clipboard?.writeText) return "unavailable";
    await navigator.clipboard.writeText(text);
    return "copied";
  } catch { return "failed"; }
}

export function ProjectDetailsDialog({ project, snapshot, isCurrent, onClose }: {
  project: WorkspaceProject; snapshot: WorkspaceSnapshot; isCurrent: () => boolean; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const alive = useRef(false);
  const copying = useRef(false);
  const composingEscape = useRef(false);
  const closing = useRef(false);
  const [copyBusy, setCopyBusy] = useState(false);
  const [feedback, setFeedback] = useState<{ result: "copied" | "unavailable" | "failed"; field: "ID" | "path" } | null>(null);
  useLayoutEffect(() => {
    const element = dialog.current;
    alive.current = true;
    element?.showModal();
    // DOM removal closes the dialog on real unmount. Closing here would manufacture
    // a close/reopen transition during StrictMode effect replay and retire its owner.
    return () => { alive.current = false; };
  }, []);
  function close() { if (!closing.current) { closing.current = true; onClose(); } }
  async function copy(field: "ID" | "path") {
    if (!isCurrent() || copying.current) return;
    copying.current = true;
    setCopyBusy(true);
    setFeedback(null);
    const result = await copyProjectValue(field === "ID" ? project.id : project.path);
    copying.current = false;
    if (alive.current && isCurrent()) { setCopyBusy(false); setFeedback({ result, field }); }
  }
  return <dialog ref={dialog} className="app-dialog session-info-dialog project-details-dialog" aria-modal="true" aria-labelledby="project-details-title" aria-describedby="project-details-description"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (!event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) close();
      else composingEscape.current = true;
    }} onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
    <header><div><span className="eyebrow">{t("Selected project")}</span><h2 id="project-details-title">{t("Project details")}</h2></div>
      <button autoFocus type="button" className="icon-button" aria-label={t("Close project details")} onClick={close}><AppIcon name="close" size={16} /></button></header>
    <p id="project-details-description" className="muted-text">{t("Read-only saved catalog snapshot. Branch, tags, description and source metadata are not available here.")}</p>
    <dl className="session-info-fields project-details-fields" tabIndex={0} aria-label={t("Recorded project information")}>
      <div><dt>{t("Project ID")}</dt><dd><code>{project.id}</code></dd></div>
      <div><dt>{t("Display name")}</dt><dd>{project.name}</dd></div>
      <div><dt>{t("Catalog path")}</dt><dd><code>{project.path}</code></dd></div>
      <div><dt>{t("Archived")}</dt><dd>{t(project.archived ? "Yes (read-only project)" : "No")}</dd></div>
    </dl>
    {snapshot.projectsTruncated && <p className="muted-text">{t("Project list is partial; omitted projects are not shown. No total session count is available.")}</p>}
    {snapshot.sessionsTruncated && <p className="muted-text">{t("Session list is partial; no session totals are reported.")}</p>}
    {snapshot.displayTextTruncated && <p className="muted-text">{t("Some display text was shortened in this bounded snapshot; this name may be shortened.")}</p>}
    <footer><span>{feedback && <span role={feedback.result === "copied" ? "status" : "alert"}>
      {feedback.result === "copied" ? t(feedback.field === "ID" ? "Project ID copied." : "Project path copied.") : feedback.result === "unavailable"
        ? t("Clipboard unavailable; nothing copied.") : t(feedback.field === "ID" ? "Could not copy project ID." : "Could not copy project path.")}
    </span>}</span><span><button type="button" className="quiet-button" disabled={copyBusy} onClick={() => void copy("ID")}>{t("Copy project ID")}</button>{" "}
      <button type="button" className="quiet-button" disabled={copyBusy} onClick={() => void copy("path")}>{t("Copy project path")}</button>{" "}
      <button type="button" className="quiet-button" onClick={close}>{t("Close")}</button></span></footer>
  </dialog>;
}

export function ProjectDetailsEntry({ context, getCurrent }: { context: ProjectDetailsContext; getCurrent: () => ProjectDetailsContext }) {
  const { t } = useShellLanguage();
  const [opened, setOpened] = useState<{ context: ProjectDetailsContext; project: WorkspaceProject } | null>(null);
  const origin = useRef<HTMLButtonElement | null>(null);
  const project = selectedProjectDetails(context);
  function isCurrent() {
    return !!opened && sameContext(opened.context, context) && sameContext(opened.context, getCurrent())
      && !!savedProjectSelection(opened.project, getCurrent().snapshot);
  }
  const visible = !!opened && isCurrent();
  useEffect(() => { if (opened && !visible) setOpened(null); }, [opened, visible]);
  function close() {
    const restore = isCurrent();
    setOpened(null);
    if (restore) requestAnimationFrame(() => {
      if (origin.current?.isConnected && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) origin.current.focus();
    });
  }
  return <><button type="button" className="quiet-button project-details-trigger" disabled={!project || !sameContext(context, getCurrent())}
    aria-haspopup="dialog" aria-expanded={visible} onClick={event => {
      if (document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]') || !sameContext(context, getCurrent())) return;
      const selected = selectedProjectDetails(getCurrent());
      if (!selected || !project || !savedProjectSelection(project, getCurrent().snapshot)) return;
      origin.current = event.currentTarget;
      setOpened({ context, project: { ...selected } });
    }} aria-label={t("Details")} title={t("Details")}><AppIcon name="info" size={14} /></button>
    {visible && opened?.context.snapshot && <ProjectDetailsDialog project={opened.project} snapshot={opened.context.snapshot} isCurrent={isCurrent} onClose={close} />}</>;
}
