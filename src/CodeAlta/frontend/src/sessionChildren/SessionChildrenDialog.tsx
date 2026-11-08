import { Button, Callout, Checkbox, Tag } from "@blueprintjs/core";
import { useEffect, useRef, useState } from "react";
import type { WorkspaceProject, WorkspaceSession } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { ConfirmPopover } from "../ConfirmPopover";
import { MachineChip } from "../explorer/ExplorerSessions";
import { plainTitle } from "../sessionTitle";
import { useShellLanguage } from "../shellLanguage";
import { SpaceIcon } from "../spaces/SpaceViews";
import { sessionHome, type Space } from "../spaces/spaces";
import { deletedSessionsWorktrees, type DeletedWorktree } from "../worktrees/worktrees";
import { sessionsAtWork, sessionTreeMessage, treeDeletable, treeSessions, type SessionChildren, type SessionChildrenRead, type SessionTreeDeletion } from "./sessionChildren";

/**
 * The sessions below a session, wherever they are: in other projects, in the chats, in other spaces. A row opens its
 * session. Delete removes one session and never a session that others come from: the window says so, and offers to
 * delete the session with all of them, asked about on its own. The worktrees they work in are kept, unless the question
 * is told to remove them too.
 */
export function SessionChildrenDialog({ title, deleting, read, projects, sessions, spaces, canDelete, checkWorktrees, onOpen, onDelete, onClose }: {
  /** The title of the session, shown until the host has read it. */
  title: string;
  /** The window comes from Delete: the session was not deleted because of its children. */
  deleting: boolean;
  read: () => Promise<SessionChildrenRead>;
  projects: readonly WorkspaceProject[];
  /** The sessions the window knows: a worktree one of them works in, outside the tree, is not removed with it. */
  sessions: readonly WorkspaceSession[];
  spaces: readonly Space[];
  /** The host can delete sessions now. */
  canDelete: boolean;
  /**
   * Asks the host who works in the worktrees of the tree, `deleted` the sessions the deletion deletes: it knows of
   * sessions the window does not list. Without it, or when it fails, the question says what the window found.
   */
  checkWorktrees?: (worktrees: readonly DeletedWorktree[], deleted: readonly string[]) => Promise<readonly DeletedWorktree[]>;
  onOpen: (session: WorkspaceSession) => void;
  /** Deletes the tree, then removes the worktrees given, those the sessions that were deleted leave. */
  onDelete: (tree: SessionChildren, worktrees: readonly DeletedWorktree[]) => Promise<SessionTreeDeletion>;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [tree, setTree] = useState<SessionChildren | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [removingWorktrees, setRemovingWorktrees] = useState(false);
  // The worktrees as the host checked them for the question that is open; null while it checks, or without a check.
  const [checked, setChecked] = useState<readonly DeletedWorktree[] | null>(null);
  const [checking, setChecking] = useState(false);
  const checks = useRef(0);
  const [message, setMessage] = useState<string | null>(null);
  const reads = useRef(0);
  const alive = useRef(true);
  const readLatest = useRef(read); readLatest.current = read;
  async function load() {
    const sequence = ++reads.current;
    setLoading(true);
    const result = await readLatest.current();
    if (!alive.current || sequence !== reads.current) return;
    setLoading(false);
    if (result.kind === "read") { setTree(result.value); setError(null); }
    else setError(t(result.code === "session_missing" ? "This session no longer exists." : "The child sessions could not be read."));
  }
  useEffect(() => {
    alive.current = true;
    void load();
    return () => { alive.current = false; };
  }, []);

  async function ask() {
    if (!tree) return;
    setRemovingWorktrees(false);
    setChecked(null);
    setConfirming(true);
    if (!checkWorktrees || found.length === 0) return;
    const sequence = ++checks.current;
    setChecking(true);
    const result = await checkWorktrees(found, [tree.root.id, ...tree.descendantIds]).catch(() => null);
    if (!alive.current || sequence !== checks.current) return;
    setChecking(false);
    if (result) setChecked(result);
  }

  async function remove() {
    if (!tree || busy) return;
    setBusy(true);
    setMessage(null);
    const result = await onDelete(tree, removingWorktrees ? worktrees.filter(worktree => !worktree.shared) : []);
    if (!alive.current) return;
    setBusy(false);
    setConfirming(false);
    if (result.kind === "deleted") { onClose(); return; }
    setMessage(t(sessionTreeMessage(result.code)));
    if (result.code !== "busy" && result.code !== "stale_epoch") void load();
  }

  const root = tree?.root;
  const atWork = tree ? sessionsAtWork(tree) : [];
  const deletable = !!tree && canDelete && treeDeletable(tree) && !busy && !loading;
  const found = tree ? deletedSessionsWorktrees(sessions, treeSessions(tree, sessions)) : [];
  const worktrees = checked ?? found;
  const removable = worktrees.filter(worktree => !worktree.shared).length;
  // Worktrees of several projects can have the same name: each says its project.
  const severalProjects = new Set(worktrees.map(worktree => worktree.projectId)).size > 1;
  const projectName = (id: string) => projects.find(project => project.id === id)?.name ?? t("Project");
  function where(session: WorkspaceSession) {
    const project = session.scopeKind === "project" ? projects.find(value => value.id === session.projectId) : undefined;
    const home = spaces.length > 1 ? sessionHome(spaces, { spaceId: session.spaceId, projectId: session.scopeKind === "project" ? session.projectId : null }) : null;
    return <span className="session-children-where">
      <span className="session-children-scope">{session.scopeKind === "project" ? project?.name ?? t("Project") : t("Chat")}</span>
      {home && <span className="session-children-space" title={home.name}><SpaceIcon space={home} size={12} />{home.name}</span>}
    </span>;
  }
  function row(session: WorkspaceSession, depth: number, working: boolean, parent: string | null) {
    const name = plainTitle(session.title);
    return <li key={session.id} className={depth === 0 ? "session-children-root" : undefined}>
      <button type="button" className="session-children-row" style={{ paddingInlineStart: 10 + depth * 18 }} title={name}
        aria-label={parent ? t("{title}, child of {parent}", { title: name, parent }) : name} onClick={() => onOpen(session)}>
        <span className="reference-icon"><AppIcon name={session.scopeKind === "project" ? "folder" : "chat"} size={14} /></span>
        <span className="session-children-title">{name}</span>
        {session.machineId && <MachineChip session={session} />}
        {working && <Tag minimal round intent="primary" className="session-children-work">{t("At work")}</Tag>}
        {where(session)}
      </button>
    </li>;
  }
  const titles = new Map(tree ? [[tree.root.id, plainTitle(tree.root.title)], ...tree.children.map(child => [child.session.id, plainTitle(child.session.title)] as const)] : []);

  return <AppWindow storageKey="codealta.desktop.window.session-children.v1" className="session-children-dialog" titleId="session-children-title"
    title={<><AppIcon name="tree" size={14} /> {t("Child sessions of {title}", { title: root ? plainTitle(root.title) : title })}</>}
    preferredSize={viewport => ({ width: Math.min(640, viewport.width - 40), height: Math.min(480, viewport.height - 40) })} minimumSize={{ width: 380, height: 260 }}
    keepOnOutsidePress={confirming || busy}
    onClose={onClose} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); if (!confirming) onClose(); }}
    onKeyDown={event => event.stopPropagation()}
    onOpened={() => document.getElementById("session-children-list")?.querySelector<HTMLButtonElement>("button")?.focus()}
    headerActions={<span className="reference-status" role="status">{(loading || busy) && <ActivitySpinner size={12} />}
      {tree && <span>{t("{direct} direct · {total} in all", { direct: tree.direct, total: tree.total })}</span>}</span>}>
    {deleting
      ? <Callout className="session-children-note" intent="warning" compact icon={null}>{t("Delete removes this session only, and not while it has child sessions: they would lose their parent. Delete it with all of them, or delete them first.")}</Callout>
      : <p className="session-children-note bp6-text-muted">{t("Delete removes one session only, and not while it has child sessions.")}</p>}
    {error && <p className="reference-empty" role="alert">{error}</p>}
    {tree && <ul id="session-children-list" className="reference-list session-children-list" aria-label={t("Child sessions")}>
      {row(tree.root, 0, tree.rootAtWork, null)}
      {tree.children.map(child => row(child.session, child.depth, child.atWork, titles.get(child.parentSessionId) ?? null))}
      {tree.total === 0 && <li className="reference-empty" role="status">{t("No child sessions.")}</li>}
    </ul>}
    <footer className="session-children-footer">
      <span className="session-children-status" role="status">
        {message ?? (atWork.length > 0 ? t("Wait for the sessions at work to finish.")
          : tree?.truncated ? t("Only the first {count} are shown.", { count: tree.children.length }) : "")}
      </span>
      {tree && tree.total > 0 && <span className="session-children-delete">
        <Button intent="danger" variant="outlined" size="small" icon={<AppIcon name="trash" size={14} />} text={t("Delete with all child sessions…")}
          disabled={!deletable} onClick={() => void ask()} />
        {confirming && <ConfirmPopover always intent="danger" busy={busy} title={t("Delete {count} sessions?", { count: tree.total + 1 })}
          subject={plainTitle(tree.root.title)} confirmLabel={t("Delete all")}
          detail={tree.total === 1 ? t("This session and the session below it are deleted with their history. Projects and their files are kept.")
            : t("This session and the {count} sessions below it are deleted with their history. Projects and their files are kept.", { count: tree.total })}
          onConfirm={() => void remove()} onCancel={() => { checks.current++; setChecking(false); setConfirming(false); }}>
          {worktrees.length > 0 && <div className="confirm-worktree">
            {worktrees.length === 1
              ? <p>{t("These sessions work in the worktree {name}, in {path}. It is kept, with its branch.", { name: worktrees[0].name, path: worktrees[0].path })}
                {worktrees[0].shared && <> {t("Other sessions work in this worktree: it stays.")}</>}</p>
              : <><p>{t("These sessions work in {count} worktrees. They are kept, with their branches:", { count: worktrees.length })}</p>
                <ul className="session-children-worktrees">{worktrees.map(worktree => <li key={worktree.path} title={worktree.path}>
                  {worktree.name}{severalProjects && <span className="muted-text"> · {projectName(worktree.projectId)}</span>}{worktree.shared && <span className="muted-text"> · {t("Other sessions work in it: it stays.")}</span>}</li>)}</ul></>}
            {checking && <p className="muted-text" role="status">{t("Checking which sessions still work in them…")}</p>}
            {removable > 0 && <Checkbox checked={removingWorktrees} disabled={busy || checking}
              label={removable === 1 ? t("Also remove the worktree") : t("Also remove the {count} worktrees", { count: removable })}
              onChange={event => setRemovingWorktrees(event.currentTarget.checked)} />}
            {removingWorktrees && removable > 0 && !checking && <p className="muted-text">
              {t("A worktree that holds changes that are not committed is kept, and its branch is deleted only when its commits are all in another branch.")}</p>}
          </div>}
        </ConfirmPopover>}
      </span>}
    </footer>
  </AppWindow>;
}
