import { useEffect, useState } from "react";
import type { ProjectGitStatusRequest } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { projectGitStatus, type ProjectGitView } from "./projectGit";
import { useShellLanguage } from "./shellLanguage";

const refreshMilliseconds = 10_000;
type Read = (request: ProjectGitStatusRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;

/**
 * The working environment of the composer: the project folder and, when it is a git repository, the current
 * branch and the lines changed against HEAD. The git facts are read when the composer appears, every ten
 * seconds while it stays, and when `refreshKey` changes (a turn ended).
 */
export function ProjectContext({ epoch, project, read, refreshKey }: {
  epoch: string | null; project: Readonly<{ id: string; name: string; path: string }>; read: Read; refreshKey?: unknown;
}) {
  const { t } = useShellLanguage();
  const [git, setGit] = useState<{ projectId: string; view: ProjectGitView | null }>();
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    let reading = false;
    const refresh = () => {
      if (reading || document.visibilityState === "hidden") return;
      reading = true;
      void read({ expectedEpoch: epoch, projectId: project.id }, { signal: abort.signal, timeoutMilliseconds: 8000 })
        .then(reply => { if (!abort.signal.aborted) setGit({ projectId: project.id, view: projectGitStatus(reply, project.id) }); },
          () => { /* The branch simply stays as last read. */ })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    window.addEventListener("focus", refresh);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener("focus", refresh); };
  }, [epoch, project.id, read, refreshKey]);
  const view = git?.projectId === project.id ? git.view : null;
  return <div className="project-context" aria-label={t("Working folder")}>
    <span className="project-context-folder" title={project.path}><AppIcon name="folder" size={14} /><strong>{project.name}</strong><span>{project.path}</span></span>
    {view && <span className="project-context-branch" title={t(view.detached ? "Detached at {branch}" : "Branch {branch}", { branch: view.branch })}>
      <AppIcon name="branch" size={14} /><span>{view.branch}</span></span>}
    {view?.changes && (view.changes.insertions > 0 || view.changes.deletions > 0) && <span className="project-context-changes"
      title={t("{files} changed file(s) against HEAD", { files: view.changes.files })}>
      <span data-change="added">+{view.changes.insertions}</span><span data-change="removed">−{view.changes.deletions}</span></span>}
  </div>;
}
