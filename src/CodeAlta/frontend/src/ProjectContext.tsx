import { useEffect, useRef, useState } from "react";
import type { ProjectGitStatusRequest } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { projectGitStatus, type ProjectGitView } from "./changes/projectGit";
import { useShellLanguage } from "./shellLanguage";
import { BranchSwitcher, type BranchApi } from "./worktrees/BranchSwitcher";
import { WorkPlaceChoice, type WorkPlaceApi } from "./worktrees/WorkPlaceChoice";
import type { WorkPlace } from "./worktrees/worktrees";

const refreshMilliseconds = 2000;
type Read = (request: ProjectGitStatusRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;

/**
 * The working environment of the composer: the project and the folder the session works in, which is the
 * folder of the project or a git worktree of it, and, when the folder is in a git repository, the current
 * branch and the lines changed against HEAD. The git facts are read when the composer appears, every two
 * seconds while it is on screen (changes made outside the application show up too), and when `refreshKey`
 * changes (a turn ended). The counts open the changes of that folder, and the branch opens the branches the
 * folder can move to. Before a session exists, a chip chooses where it will work.
 */
export function ProjectContext({ epoch, project, read, refreshKey, worktree = null, place, onWorktreeGone, onShowChanges, onOpenTerminal, branches }: {
  epoch: string | null; project: Readonly<{ id: string; name: string; path: string }>; read: Read; refreshKey?: unknown;
  /** The git worktree the session works in, as the session records it; null for the folder of the project. */
  worktree?: Readonly<{ path: string; name: string; missing: boolean }> | null;
  /** Before a session exists: where it will work, and how that is changed. */
  place?: Readonly<{ value: WorkPlace; onChange: (place: WorkPlace) => void; onOpenSettings?: () => void; api?: WorkPlaceApi }>;
  /** The folder of the worktree is not there any more: the sessions are read again. */
  onWorktreeGone?: () => void;
  /** Opens the changes tab of the project; without it the counts are only shown. */
  onShowChanges?: () => void;
  /** Opens a terminal in the folder the composer works in. */
  onOpenTerminal?: () => void;
  /** Lets the branch be changed from its chip; false where the window cannot change it. */
  branches?: boolean | BranchApi;
}) {
  const { t, locale } = useShellLanguage();
  // The checkout that is read: the worktree while its folder is there, the folder of the project otherwise.
  const folder = worktree && !worktree.missing ? worktree.path : null;
  const key = `${project.id}\n${folder ?? ""}`;
  const [git, setGit] = useState<{ key: string; view: ProjectGitView | null }>();
  const [switched, setSwitched] = useState(0);
  const element = useRef<HTMLDivElement>(null);
  const gone = useRef(onWorktreeGone); gone.current = onWorktreeGone;
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    let reading = false, reported = false;
    const refresh = () => {
      // A composer of a tab that is not shown, or of a window that is, reads nothing.
      if (reading || document.visibilityState === "hidden" || !element.current?.offsetParent) return;
      reading = true;
      void read({ expectedEpoch: epoch, projectId: project.id, worktree: folder }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
        .then(reply => {
          if (abort.signal.aborted) return;
          // A worktree can be removed at any time, from here or from outside: the list of sessions then says so.
          if (folder && !reported && (reply as { status?: unknown } | null)?.status === "worktree_missing") { reported = true; gone.current?.(); }
          const view = projectGitStatus(reply, project.id);
          // The same facts again keep the state they are in: nothing is drawn.
          setGit(current => current?.key === key && sameView(current.view, view) ? current : { key, view });
        }, () => { /* The branch simply stays as last read. */ })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    window.addEventListener("focus", refresh);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener("focus", refresh); };
  }, [epoch, project.id, folder, key, read, refreshKey, switched]);
  const view = git?.key === key ? git.view : null;
  const changes = view?.changes && (view.changes.insertions > 0 || view.changes.deletions > 0 || view.changes.files > 0) ? view.changes : null;
  const counts = changes && <><span data-change="added">+{changes.insertions.toLocaleString(locale)}</span><span data-change="removed">−{changes.deletions.toLocaleString(locale)}</span></>;
  const title = changes ? t("{files} changed file(s) against HEAD", { files: changes.files }) : t("Show changes");
  const branchTitle = view ? t(view.detached ? "Detached at {branch}" : "Branch {branch}", { branch: view.branch }) : "";
  const branch = view && <><AppIcon name="branch" size={14} /><span>{view.branch}</span></>;
  return <div ref={element} className="project-context" aria-label={t("Working folder")}>
    <span className="project-context-folder" title={project.path}><AppIcon name="folder" size={14} /><strong>{project.name}</strong>{!worktree && <span>{project.path}</span>}</span>
    {worktree && (() => {
      const about = worktree.missing ? `${t("The folder of this worktree is gone: the session continues in the folder of the project.")}\n${worktree.path}`
        : `${t("Works in a git worktree of {project}", { project: project.name })}\n${worktree.path}`;
      const content = <><AppIcon name="worktree" size={14} /><strong>{worktree.name}</strong><span>{worktree.missing ? t("removed") : worktree.path}</span></>;
      // The worktree leads to its changes, where the checkouts of the project are listed and removed.
      return onShowChanges && !worktree.missing
        ? <button type="button" className="project-context-worktree" title={`${about}\n${t("Show changes")}`} onClick={onShowChanges}>{content}</button>
        : <span className="project-context-worktree" data-missing={worktree.missing || undefined} title={about}>{content}</span>;
    })()}
    {/* Offered where there is a repository; a worktree that was chosen is never hidden, so that it can be changed. */}
    {place && epoch && (view || place.value.worktree) && <WorkPlaceChoice epoch={epoch} projectId={project.id} branch={view?.branch ?? null} place={place.value}
      onPlace={place.onChange} onOpenSettings={place.onOpenSettings} api={place.api} />}
    {view && (branches && epoch
      ? <BranchSwitcher epoch={epoch} projectId={project.id} worktree={folder} className="project-context-branch" title={`${branchTitle}\n${t("Switch branch")}`}
        api={branches === true ? undefined : branches} onSwitched={() => setSwitched(value => value + 1)}>{branch}</BranchSwitcher>
      : <span className="project-context-branch" title={branchTitle}>{branch}</span>)}
    {view && (onShowChanges
      ? <button type="button" className="project-context-changes" title={`${title}\n${t("Show changes")}`} aria-label={t("Show changes")} onClick={onShowChanges}>
        <AppIcon name="changes" size={13} />{counts}</button>
      : changes && <span className="project-context-changes" title={title}>{counts}</span>)}
    {onOpenTerminal && <button type="button" className="project-context-terminal" title={t("New terminal")} aria-label={t("New terminal")} onClick={onOpenTerminal}>
      <AppIcon name="terminal" size={13} /></button>}
  </div>;
}

function sameView(a: ProjectGitView | null, b: ProjectGitView | null) {
  return a === b || !!a && !!b && a.branch === b.branch && a.detached === b.detached && (a.changes === b.changes || !!a.changes && !!b.changes
    && a.changes.insertions === b.changes.insertions && a.changes.deletions === b.changes.deletions && a.changes.files === b.changes.files);
}
