import { AppWindowSurface } from "./AppWindow";
import { AppIcon } from "./AppIcon";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { savedProjectSelection } from "./savedProjectSelection";
import { useShellLanguage } from "./shellLanguage";

export type ProjectDetailsContext = Readonly<{ snapshot: WorkspaceSnapshot | undefined; projectId: string | null;
  sessionId: string | null; hostEpoch: string | null; hostAvailable: boolean;
  refreshVersion: number; refreshReady: boolean; active: boolean }>;

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

type CopyResult = "copied" | "unavailable" | "failed";

// A value of the list with a copy button beside it; the button shows the outcome for a moment.
function CopyValue({ value, label, isCurrent }: { value: string; label: string; isCurrent: () => boolean }) {
  const { t } = useShellLanguage();
  const [result, setResult] = useState<CopyResult | null>(null);
  const alive = useRef(true);
  const reset = useRef<number | undefined>(undefined);
  useEffect(() => { alive.current = true; return () => { alive.current = false; window.clearTimeout(reset.current); }; }, []);
  async function copy() {
    if (!isCurrent()) return;
    const outcome = await copyProjectValue(value);
    if (!alive.current || !isCurrent()) return;
    setResult(outcome);
    window.clearTimeout(reset.current);
    reset.current = window.setTimeout(() => { if (alive.current) setResult(null); }, 1600);
  }
  const title = result === "copied" ? t("Copied") : result === "unavailable" ? t("Clipboard unavailable; nothing copied.") : result === "failed" ? t("Copy failed") : label;
  return <button type="button" className={`copy-value${result ? ` copy-${result}` : ""}`} aria-label={title} title={title} onClick={() => void copy()}>
    <AppIcon name={result === "copied" ? "checked" : result ? "error" : "copy"} size={14} />
    <span className="sr-only" aria-live="polite">{result ? title : ""}</span>
  </button>;
}

export function ProjectDetailsDialog({ project, isCurrent, onClose }: {
  project: WorkspaceProject; snapshot?: WorkspaceSnapshot; isCurrent: () => boolean; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const composingEscape = useRef(false);
  const closing = useRef(false);
  useLayoutEffect(() => {
    // DOM removal closes the dialog on real unmount. Closing here would manufacture
    // a close/reopen transition during StrictMode effect replay and retire its owner.
    dialog.current?.showModal();
  }, []);
  function close() { if (!closing.current) { closing.current = true; onClose(); } }
  return <dialog ref={dialog} className="app-dialog session-info-dialog project-details-dialog" aria-modal="true" aria-labelledby="project-details-title"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (!event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) close();
      else composingEscape.current = true;
    }} onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
    <AppWindowSurface storageKey="codealta.desktop.window.project-details.v2" title={t("Project details")} titleId="project-details-title" preferredSize={viewport => ({ width: Math.min(600, viewport.width - 40), height: Math.min(300, viewport.height - 40) })}
      onClose={close} closeLabel={t("Close project details")}>
    <dl className="session-info-fields project-details-fields" aria-label={t("Recorded project information")}>
      <div><dt>{t("Display name")}</dt><dd>{project.name}</dd><CopyValue value={project.name} label={t("Copy project name")} isCurrent={isCurrent} /></div>
      <div><dt>{t("Catalog path")}</dt><dd><code>{project.path}</code></dd><CopyValue value={project.path} label={t("Copy project path")} isCurrent={isCurrent} /></div>
      <div><dt>{t("Project ID")}</dt><dd><code>{project.id}</code></dd><CopyValue value={project.id} label={t("Copy project ID")} isCurrent={isCurrent} /></div>
      <div><dt>{t("Archived")}</dt><dd>{t(project.archived ? "Yes (read-only project)" : "No")}</dd></div>
    </dl>
  </AppWindowSurface></dialog>;
}
