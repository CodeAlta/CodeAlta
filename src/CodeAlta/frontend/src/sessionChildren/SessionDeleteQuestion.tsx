import { Checkbox } from "@blueprintjs/core";
import { useEffect, useState, type RefObject } from "react";
import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { inCloud } from "../cloudSessions/cloudSessions";
import { ConfirmPopover } from "../ConfirmPopover";
import { plainTitle } from "../sessionTitle";
import type { useShellLanguage } from "../shellLanguage";
import { deletedSessionWorktree, type DeletedWorktree } from "../worktrees/worktrees";

type WorktreeToRemove = Pick<DeletedWorktree, "name" | "path">;

/** Keeps the Explorer question and its optional worktree choice for a single-session deletion. */
export function useSessionDeleteQuestion({ railVisible, pending, alive, project, catalog, busy, checkWorktrees, onDelete, t }: {
  railVisible: boolean; pending: RefObject<boolean>; alive: RefObject<boolean>;
  project: WorkspaceProject | undefined; catalog: RefObject<WorkspaceSnapshot | undefined>; busy: boolean;
  checkWorktrees: (worktrees: readonly DeletedWorktree[], deleted: readonly string[]) => Promise<readonly DeletedWorktree[]>;
  onDelete: (session: WorkspaceSession, remember: boolean, worktree: WorktreeToRemove | null) => void;
  t: ReturnType<typeof useShellLanguage>["t"];
}) {
  const [id, setId] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [removeWorktree, setRemoveWorktree] = useState(false);
  // Null sharing means the host is still checking sessions the window may not list.
  const [check, setCheck] = useState<{ id: string; shared: boolean | null } | null>(null);
  const close = () => setId(null);
  const worktreeOf = (session: WorkspaceSession) => project && !project.archived
    ? deletedSessionWorktree(catalog.current?.sessions ?? [], session) : null;

  // When the Explorer goes, the question goes with its row, unless a deletion is in progress.
  useEffect(() => { if (!railVisible && !pending.current) setId(null); }, [railVisible, pending]);

  function ask(session: WorkspaceSession) {
    setRemoveWorktree(false);
    setCheck(null);
    setId(session.id);
    const worktree = worktreeOf(session);
    if (!worktree || worktree.shared || !project) return;
    setCheck({ id: session.id, shared: null });
    void checkWorktrees([{ projectId: project.id, ...worktree, sessionIds: [session.id] }], [session.id])
      .catch(() => null)
      .then(checked => {
        if (alive.current) setCheck(value => value?.id === session.id ? { id: session.id, shared: checked?.[0]?.shared ?? false } : value);
      });
  }

  function render(session: WorkspaceSession) {
    if (id !== session.id) return null;
    // A session that works in a worktree leaves it, with its branch: the question says where, and can remove it.
    const found = worktreeOf(session);
    const sharing = check?.id === session.id ? check.shared : undefined;
    const worktree = found && sharing ? { ...found, shared: true } : found;
    const checking = !!found && !found.shared && sharing === null;
    return <ConfirmPopover title={t("Delete this session?")} subject={plainTitle(session.title)}
      detail={inCloud(session) ? `${t("Its history is deleted and cannot be restored. The files of the project are kept.")} ${t("The cloud session stays on claude.ai: archive or delete it there.")}`
        : t("Its history is deleted and cannot be restored. The files of the project are kept.")}
      confirmLabel={t("Delete")} intent="danger" busy={busy} error={error || null}
      onConfirm={remember => onDelete(session, remember, worktree && !worktree.shared && removeWorktree ? worktree : null)}
      onCancel={() => { close(); setError(""); }}>
      {worktree && <div className="confirm-worktree">
        <p>{t("Its worktree {name} is kept in {path}, with its branch.", { name: worktree.name, path: worktree.path })}</p>
        {worktree.shared ? <p>{t("Other sessions work in this worktree: it stays.")}</p>
          : <>{checking && <p className="muted-text" role="status">{t("Checking which sessions still work in it…")}</p>}
            <Checkbox checked={removeWorktree} disabled={busy || checking} label={t("Also remove the worktree")}
              onChange={event => setRemoveWorktree(event.currentTarget.checked)} /></>}
        {removeWorktree && !worktree.shared && !checking && <p className="muted-text">
          {t("A worktree that holds changes that are not committed is kept, and its branch is deleted only when its commits are all in another branch.")}</p>}
      </div>}
    </ConfirmPopover>;
  }

  return { ask, close, setError, render };
}
