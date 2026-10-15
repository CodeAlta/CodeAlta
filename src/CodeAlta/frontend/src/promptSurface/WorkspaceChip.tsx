import { useEffect, useState, type ReactNode } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useProjectGit, type ProjectGitRead } from "../ProjectContext";
import { useShellLanguage } from "../shellLanguage";
import { BranchSwitcher, type BranchApi } from "../worktrees/BranchSwitcher";
import { WorkPlaceChoice, type WorkPlaceApi } from "../worktrees/WorkPlaceChoice";
import type { WorkPlace } from "../worktrees/worktrees";

/**
 * Where the session works, above its prompt: the project, the worktree or the branch with the lines changed against
 * HEAD, and a button that opens a terminal there. The project and the branch open the Workspace popover: project,
 * worktree, branch (which can be switched), changes, path with a copy button, and the ways to the changes, a terminal
 * and a pull request. Before a session exists, the popover also chooses where it will work.
 */
export function WorkspaceChip({ epoch, project, read, refreshKey, worktree = null, place, onWorktreeGone, onShowChanges, onOpenTerminal, branches, pullRequest }: {
  epoch: string | null; project: Readonly<{ id: string; name: string; path: string }>; read: ProjectGitRead; refreshKey?: unknown;
  /** The git worktree the session works in, as the session records it; null for the folder of the project. */
  worktree?: Readonly<{ path: string; name: string; missing: boolean }> | null;
  /** Before a session exists: where it will work, and how that is changed. */
  place?: Readonly<{ value: WorkPlace; onChange: (place: WorkPlace) => void; onOpenSettings?: () => void; api?: WorkPlaceApi }>;
  onWorktreeGone?: () => void;
  /** Opens the changes of the folder; without it the counts are only shown. */
  onShowChanges?: () => void;
  /** Opens a terminal in the folder. */
  onOpenTerminal?: () => void;
  /** Lets the branch be changed; false where the window cannot change it. */
  branches?: boolean | BranchApi;
  /** What asks the session for a pull request; shown where there is a repository. */
  pullRequest?: ReactNode;
}) {
  const { t, locale } = useShellLanguage();
  const { folder, view, element, switched } = useProjectGit(epoch, project.id, worktree, read, refreshKey, onWorktreeGone);
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState<boolean | null>(null);
  useEffect(() => { if (copied === null) return; const timer = setTimeout(() => setCopied(null), 1600); return () => clearTimeout(timer); }, [copied]);
  const changes = view?.changes && (view.changes.insertions > 0 || view.changes.deletions > 0 || view.changes.files > 0) ? view.changes : null;
  const counts = changes && <span className="workspace-chip-counts"><span data-change="added">+{changes.insertions.toLocaleString(locale)}</span>
    <span data-change="removed">−{changes.deletions.toLocaleString(locale)}</span></span>;
  const path = folder ?? project.path;
  const newWorktree = !!place?.value.worktree;
  const place2 = worktree
    ? { icon: "worktree" as const, name: worktree.name, title: worktree.missing ? t("The folder of this worktree is gone: the session continues in the folder of the project.") : t("Works in a git worktree of {project}", { project: project.name }) }
    : newWorktree ? { icon: "worktree" as const, name: t("New worktree"), title: t("The next session works in a new git worktree.") }
    : view ? { icon: "branch" as const, name: view.branch, title: t(view.detached ? "Detached at {branch}" : "Branch {branch}", { branch: view.branch }) }
    : null;
  const branchLabel = view && <><AppIcon name="branch" size={14} /><span>{view.branch}</span><AppIcon name="chevronDown" size={13} /></>;
  const popover = <div className="workspace-popover" role="group" aria-label={t("Workspace")}>
    <header><AppIcon name="folder" size={16} /><strong>{t("Workspace")}</strong></header>
    <dl>
      <dt>{t("Project")}</dt><dd><strong>{project.name}</strong></dd>
      {worktree && <><dt>{t("Worktree")}</dt><dd data-missing={worktree.missing || undefined}><AppIcon name="worktree" size={14} /><span>{worktree.name}</span>
        {worktree.missing && <small>{t("removed")}</small>}</dd></>}
      {place && epoch && (view || place.value.worktree) && <><dt>{t("Works in")}</dt><dd><WorkPlaceChoice epoch={epoch} projectId={project.id} branch={view?.branch ?? null}
        place={place.value} onPlace={place.onChange} onOpenSettings={place.onOpenSettings} api={place.api} /></dd></>}
      {view && <><dt>{t("Branch")}</dt><dd>{branches && epoch
        ? <BranchSwitcher epoch={epoch} projectId={project.id} worktree={folder} className="workspace-branch" placement="bottom-start"
          title={t("Switch branch")} api={branches === true ? undefined : branches} onSwitched={switched}>{branchLabel}</BranchSwitcher>
        : <span className="workspace-branch"><AppIcon name="branch" size={14} /><span>{view.branch}</span></span>}</dd></>}
      {view && <><dt>{t("Changes")}</dt><dd>{changes ? <>{counts}<small>{t("{files} changed file(s) against HEAD", { files: changes.files })}</small></> : <small>{t("No changes")}</small>}</dd></>}
      <dt>{t("Path")}</dt><dd className="workspace-path"><code title={path}>{path}</code>
        <Button variant="minimal" size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={t("Copy the path")} title={t("Copy the path")}
          onClick={() => void navigator.clipboard.writeText(path).then(() => setCopied(true), () => setCopied(false))} /></dd>
    </dl>
    {/* The ways out of the popover, the work first (changes, pull request), then the folder (terminal). */}
    {(onShowChanges || onOpenTerminal || (view && pullRequest)) && <footer>
      {onShowChanges && <Button size="small" icon={<AppIcon name="changes" size={14} />} onClick={() => { setOpen(false); onShowChanges(); }}>{t("View changes")}</Button>}
      {view && pullRequest}
      {onOpenTerminal && <Button size="small" icon={<AppIcon name="terminal" size={14} />} onClick={() => { setOpen(false); onOpenTerminal(); }}>{t("Open terminal")}</Button>}
    </footer>}
  </div>;
  return <div ref={element} className="workspace-chip" role="group" aria-label={t("Workspace")} data-worktree={worktree ? worktree.missing ? "missing" : "yes" : undefined}>
    <PopoverNext content={popover} placement="bottom-end" isOpen={open} onInteraction={next => setOpen(next)} popoverClassName="workspace-chip-popover">
      <span className="workspace-chip-open">
        <button type="button" className="workspace-chip-part workspace-chip-project" aria-haspopup="dialog" aria-expanded={open}
          aria-label={`${t("Workspace")}: ${project.name}`} title={`${project.name}\n${project.path}`}>
          <AppIcon name="folder" size={14} /><strong className="workspace-chip-name">{project.name}</strong><AppIcon name="chevronDown" size={13} className="workspace-chip-caret" />
        </button>
        {place2 && <button type="button" className="workspace-chip-part workspace-chip-place" aria-haspopup="dialog" aria-expanded={open}
          aria-label={`${place2.name}${changes ? ` +${changes.insertions} −${changes.deletions}` : ""}`} title={`${place2.title}\n${path}`}>
          <AppIcon name={place2.icon} size={14} /><span className="workspace-chip-name">{place2.name}</span>{counts}
          <AppIcon name="chevronDown" size={13} className="workspace-chip-caret" />
        </button>}
      </span>
    </PopoverNext>
    {onOpenTerminal && <button type="button" className="workspace-chip-part workspace-chip-terminal" title={t("New terminal")} aria-label={t("New terminal")} onClick={onOpenTerminal}>
      <AppIcon name="terminal" size={14} /></button>}
  </div>;
}
