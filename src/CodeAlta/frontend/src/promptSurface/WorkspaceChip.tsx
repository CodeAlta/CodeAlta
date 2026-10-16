import { useEffect, useState, type ReactNode } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useProjectGit, type ProjectGitRead } from "../ProjectContext";
import { useShellLanguage } from "../shellLanguage";
import { ExternalEditorIcon, openInFileManagerKey, useExternalOpener, type ExternalTarget } from "../externalEditors/externalEditors";
import { BranchSwitcher, type BranchApi } from "../worktrees/BranchSwitcher";
import { WorkPlaceChoice, type WorkPlaceApi } from "../worktrees/WorkPlaceChoice";
import type { WorkPlace } from "../worktrees/worktrees";

/**
 * Where the session works, above its prompt: the project, the worktree or the branch with the lines changed against
 * HEAD, and a button that opens a terminal there. The project and the branch open the Workspace popover: project,
 * worktree, branch (which can be switched), changes, path with a copy button, and the ways to the changes, a terminal
 * and a pull request. A session in a worktree shows it in the tone of worktrees. Before a session exists, a part of
 * the chip of its own says where it will work, the folder of the project or a new worktree, and changes it.
 */
export function WorkspaceChip({ epoch, project, sessionId = null, read, refreshKey, worktree = null, place, onWorktreeGone, onShowChanges, onOpenTerminal, branches, pullRequest }: {
  epoch: string | null; project: Readonly<{ id: string; name: string; path: string }>; read: ProjectGitRead; refreshKey?: unknown;
  /** The session the chip is about; null before it exists. The external editor and the file manager open the folder it works in. */
  sessionId?: string | null;
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
  // The popover opens below the part that was pressed: the project, or the worktree or the branch.
  const [open, setOpen] = useState<"project" | "place" | null>(null);
  const [copied, setCopied] = useState<boolean | null>(null);
  // The editor the user chose and the file manager, beside the path: asked for when the popover opens.
  const external = useExternalOpener(open !== null);
  const openExternal = (target: ExternalTarget) => { setOpen(null); void external.opener?.openFolder(target, project.id, sessionId); };
  useEffect(() => { if (copied === null) return; const timer = setTimeout(() => setCopied(null), 1600); return () => clearTimeout(timer); }, [copied]);
  const changes = view?.changes && (view.changes.insertions > 0 || view.changes.deletions > 0 || view.changes.files > 0) ? view.changes : null;
  const counts = changes && <span className="workspace-chip-counts"><span data-change="added">+{changes.insertions.toLocaleString(locale)}</span>
    <span data-change="removed">−{changes.deletions.toLocaleString(locale)}</span></span>;
  const path = folder ?? project.path;
  const branchTitle = view ? t(view.detached ? "Detached at {branch}" : "Branch {branch}", { branch: view.branch }) : null;
  // A session in a worktree says so in words: the worktree, the project it belongs to, and its branch.
  const about = worktree && (worktree.missing ? t("The folder of this worktree is gone: the session continues in the folder of the project.")
    : t("Works in the worktree {name} of {project}", { name: worktree.name, project: project.name }));
  const place2 = worktree
    ? { icon: "worktree" as const, name: worktree.name, label: about!, title: [about, branchTitle].filter(Boolean).join("\n") }
    : view ? { icon: "branch" as const, name: view.branch, label: view.branch, title: branchTitle! }
    : null;
  // Before a session exists, where it will work is a part of its own: a worktree needs a repository, and one that was chosen stays shown.
  const workPlace = place && epoch && (view || place.value.worktree) && <WorkPlaceChoice chip epoch={epoch} projectId={project.id} branch={view?.branch ?? null}
    place={place.value} onPlace={place.onChange} onOpenSettings={place.onOpenSettings} api={place.api} />;
  const branchLabel = view && <><AppIcon name="branch" size={14} /><span>{view.branch}</span><AppIcon name="chevronDown" size={13} /></>;
  const popover = <div className="workspace-popover" role="group" aria-label={t("Workspace")}>
    <header><AppIcon name="folder" size={16} /><strong>{t("Workspace")}</strong></header>
    <dl>
      <dt>{t("Project")}</dt><dd><strong>{project.name}</strong></dd>
      {worktree && <><dt>{t("Worktree")}</dt><dd data-missing={worktree.missing || undefined}><AppIcon name="worktree" size={14} /><span>{worktree.name}</span>
        {worktree.missing && <small>{t("removed")}</small>}</dd></>}
      {view && <><dt>{t("Branch")}</dt><dd>{branches && epoch
        ? <BranchSwitcher epoch={epoch} projectId={project.id} worktree={folder} className="workspace-branch" placement="bottom-start"
          title={t("Switch branch")} api={branches === true ? undefined : branches} onSwitched={switched}>{branchLabel}</BranchSwitcher>
        : <span className="workspace-branch"><AppIcon name="branch" size={14} /><span>{view.branch}</span></span>}</dd></>}
      {view && <><dt>{t("Changes")}</dt><dd>{changes ? <>{counts}<small>{t("{files} changed file(s) against HEAD", { files: changes.files })}</small></> : <small>{t("No changes")}</small>}</dd></>}
      <dt>{t("Path")}</dt><dd className="workspace-path"><code title={path}>{path}</code>
        <Button variant="minimal" size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={t("Copy the path")} title={t("Copy the path")}
          onClick={() => void navigator.clipboard.writeText(path).then(() => setCopied(true), () => setCopied(false))} />
        {external.opener && external.editor && <Button variant="minimal" size="small" icon={<ExternalEditorIcon id={external.editor.id} size={14} />}
          aria-label={t("Open in {editor}", { editor: external.editor.name })} title={t("Open in {editor}", { editor: external.editor.name })} onClick={() => openExternal("editor")} />}
        {external.opener && external.platform && <Button variant="minimal" size="small" icon={<AppIcon name="folder" size={14} />}
          aria-label={t(openInFileManagerKey(external.platform))} title={t(openInFileManagerKey(external.platform))} onClick={() => openExternal("explorer")} />}</dd>
    </dl>
    {/* The ways out of the popover, the work first (changes, pull request), then the folder (terminal). */}
    {(onShowChanges || onOpenTerminal || (view && pullRequest)) && <footer>
      {onShowChanges && <Button size="small" icon={<AppIcon name="changes" size={14} />} onClick={() => { setOpen(null); onShowChanges(); }}>{t("View changes")}</Button>}
      {view && pullRequest}
      {onOpenTerminal && <Button size="small" icon={<AppIcon name="terminal" size={14} />} onClick={() => { setOpen(null); onOpenTerminal(); }}>{t("Open terminal")}</Button>}
    </footer>}
  </div>;
  const opens = (part: "project" | "place") => (next: boolean) => setOpen(current => next ? part : current === part ? null : current);
  return <div ref={element} className="workspace-chip" role="group" aria-label={t("Workspace")} data-worktree={worktree ? worktree.missing ? "missing" : "yes" : undefined}>
    <PopoverNext content={popover} placement="bottom-end" isOpen={open === "project"} onInteraction={opens("project")} popoverClassName="workspace-chip-popover">
      <button type="button" className="workspace-chip-part workspace-chip-project" aria-haspopup="dialog" aria-expanded={open === "project"}
        aria-label={`${t("Workspace")}: ${project.name}`} title={`${project.name}\n${project.path}`}>
        <AppIcon name="folder" size={14} /><strong className="workspace-chip-name">{project.name}</strong><AppIcon name="chevronDown" size={13} className="workspace-chip-caret" />
      </button>
    </PopoverNext>
    {workPlace}
    {place2 && <PopoverNext content={popover} placement="bottom-end" isOpen={open === "place"} onInteraction={opens("place")} popoverClassName="workspace-chip-popover">
      <button type="button" className="workspace-chip-part workspace-chip-place" data-worktree={worktree ? worktree.missing ? "missing" : "yes" : undefined}
        aria-haspopup="dialog" aria-expanded={open === "place"}
        aria-label={`${place2.label}${changes ? ` +${changes.insertions} −${changes.deletions}` : ""}`} title={`${place2.title}\n${path}`}>
        <AppIcon name={place2.icon} size={14} />{worktree && <span className="workspace-chip-kind">{t("Worktree")}</span>}
        <span className="workspace-chip-name">{place2.name}</span>{counts}
        <AppIcon name="chevronDown" size={13} className="workspace-chip-caret" />
      </button>
    </PopoverNext>}
    {onOpenTerminal && <button type="button" className="workspace-chip-part workspace-chip-terminal" title={t("New terminal")} aria-label={t("New terminal")} onClick={onOpenTerminal}>
      <AppIcon name="terminal" size={14} /></button>}
  </div>;
}
