import { useEffect, useRef, useState } from "react";
import type { ProjectGitStatusRequest } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { projectGitStatus, type ProjectGitView } from "./changes/projectGit";
import { useShellLanguage } from "./shellLanguage";

const refreshMilliseconds = 2000;
type Read = (request: ProjectGitStatusRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;

/**
 * The working environment of the composer: the project folder and, when it is a git repository, the current
 * branch and the lines changed against HEAD. The git facts are read when the composer appears, every two
 * seconds while it is on screen (changes made outside the application show up too), and when `refreshKey`
 * changes (a turn ended). The counts open the changes of the project.
 */
export function ProjectContext({ epoch, project, read, refreshKey, onShowChanges }: {
  epoch: string | null; project: Readonly<{ id: string; name: string; path: string }>; read: Read; refreshKey?: unknown;
  /** Opens the changes tab of the project; without it the counts are only shown. */
  onShowChanges?: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const [git, setGit] = useState<{ projectId: string; view: ProjectGitView | null }>();
  const element = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    let reading = false;
    const refresh = () => {
      // A composer of a tab that is not shown, or of a window that is, reads nothing.
      if (reading || document.visibilityState === "hidden" || !element.current?.offsetParent) return;
      reading = true;
      void read({ expectedEpoch: epoch, projectId: project.id }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
        .then(reply => {
          if (abort.signal.aborted) return;
          const view = projectGitStatus(reply, project.id);
          // The same facts again keep the state they are in: nothing is drawn.
          setGit(current => current?.projectId === project.id && sameView(current.view, view) ? current : { projectId: project.id, view });
        }, () => { /* The branch simply stays as last read. */ })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    window.addEventListener("focus", refresh);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener("focus", refresh); };
  }, [epoch, project.id, read, refreshKey]);
  const view = git?.projectId === project.id ? git.view : null;
  const changes = view?.changes && (view.changes.insertions > 0 || view.changes.deletions > 0 || view.changes.files > 0) ? view.changes : null;
  const counts = changes && <><span data-change="added">+{changes.insertions.toLocaleString(locale)}</span><span data-change="removed">−{changes.deletions.toLocaleString(locale)}</span></>;
  const title = changes ? t("{files} changed file(s) against HEAD", { files: changes.files }) : t("Show changes");
  return <div ref={element} className="project-context" aria-label={t("Working folder")}>
    <span className="project-context-folder" title={project.path}><AppIcon name="folder" size={14} /><strong>{project.name}</strong><span>{project.path}</span></span>
    {view && <span className="project-context-branch" title={t(view.detached ? "Detached at {branch}" : "Branch {branch}", { branch: view.branch })}>
      <AppIcon name="branch" size={14} /><span>{view.branch}</span></span>}
    {view && (onShowChanges
      ? <button type="button" className="project-context-changes" title={`${title}\n${t("Show changes")}`} aria-label={t("Show changes")} onClick={onShowChanges}>
        <AppIcon name="changes" size={13} />{counts}</button>
      : changes && <span className="project-context-changes" title={title}>{counts}</span>)}
  </div>;
}

function sameView(a: ProjectGitView | null, b: ProjectGitView | null) {
  return a === b || !!a && !!b && a.branch === b.branch && a.detached === b.detached && (a.changes === b.changes || !!a.changes && !!b.changes
    && a.changes.insertions === b.changes.insertions && a.changes.deletions === b.changes.deletions && a.changes.files === b.changes.files);
}
