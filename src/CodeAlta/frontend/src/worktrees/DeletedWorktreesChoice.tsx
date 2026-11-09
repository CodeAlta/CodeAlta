import { Checkbox } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import type { DeletedWorktree } from "./worktrees";

/**
 * The worktrees sessions about to be deleted work in, in the question of their deletion: each once, the ones another
 * session works in said to stay, and a check box that removes the others with the sessions. Nothing while the
 * sessions work in none.
 */
export function DeletedWorktreesChoice({ worktrees, checking, removing, setRemoving, busy, projectName }: {
  worktrees: readonly DeletedWorktree[];
  /** The host is asked who works in them: nothing is offered until it answers. */
  checking: boolean;
  removing: boolean;
  setRemoving: (value: boolean) => void;
  busy: boolean;
  /** The name of a project, said beside each worktree when they come from several. */
  projectName: (projectId: string) => string;
}) {
  const { t } = useShellLanguage();
  if (worktrees.length === 0) return null;
  const removable = worktrees.filter(worktree => !worktree.shared).length;
  // Worktrees of several projects can have the same name: each says its project.
  const severalProjects = new Set(worktrees.map(worktree => worktree.projectId)).size > 1;
  return <div className="confirm-worktree">
    {worktrees.length === 1
      ? <p>{t("These sessions work in the worktree {name}, in {path}. It is kept, with its branch.", { name: worktrees[0].name, path: worktrees[0].path })}
        {worktrees[0].shared && <> {t("Other sessions work in this worktree: it stays.")}</>}</p>
      : <><p>{t("These sessions work in {count} worktrees. They are kept, with their branches:", { count: worktrees.length })}</p>
        <ul className="deleted-worktrees">{worktrees.map(worktree => <li key={worktree.path} title={worktree.path}>
          {worktree.name}{severalProjects && <span className="muted-text"> · {projectName(worktree.projectId)}</span>}{worktree.shared && <span className="muted-text"> · {t("Other sessions work in it: it stays.")}</span>}</li>)}</ul></>}
    {checking && <p className="muted-text" role="status">{t("Checking which sessions still work in them…")}</p>}
    {removable > 0 && <Checkbox checked={removing} disabled={busy || checking}
      label={removable === 1 ? t("Also remove the worktree") : t("Also remove the {count} worktrees", { count: removable })}
      onChange={event => setRemoving(event.currentTarget.checked)} />}
    {removing && removable > 0 && !checking && <p className="muted-text">
      {t("A worktree that holds changes that are not committed is kept, and its branch is deleted only when its commits are all in another branch.")}</p>}
  </div>;
}
