import { Button } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { stepElapsed } from "../SessionStartProgress";
import { useShellLanguage } from "../shellLanguage";
import { worktreeCreationMessage } from "../sessionCreation";
import { progressPercent, type PreparedWorktree } from "./preparedWorktrees";

/**
 * Where the worktree of the next session is while its prompt is typed, under the welcome of the New session tab: the
 * step git is at, the last line it wrote and how far it is, then ready, or why it failed with Retry and Close. It
 * never holds the prompt: only the Send waits for the worktree.
 */
export function WorktreePreparation({ worktree, onRetry, onClose }: { worktree: PreparedWorktree; onRetry: () => void; onClose: () => void }) {
  const { t } = useShellLanguage();
  if (worktree.state === "dismissed") return null;
  if (worktree.state === "ready") {
    return <section className="worktree-preparation ready" aria-label={t("The worktree {branch} is ready.", { branch: worktree.branch ?? "" })} data-state="ready">
      <p className="worktree-preparation-title"><AppIcon name="check" size={14} />{t("The worktree {branch} is ready.", { branch: worktree.branch ?? "" })}</p>
      {worktree.warning && <p className="worktree-preparation-line warning" title={worktree.warning}>
        <AppIcon name="warning" size={12} />{t("The submodules of the worktree could not be checked out: {message}", { message: worktree.warning })}</p>}
    </section>;
  }
  if (worktree.state === "failed") {
    return <section className="worktree-preparation failed" role="alert" data-state="failed">
      <p className="worktree-preparation-title"><AppIcon name="error" size={14} />{t("The worktree could not be prepared.")}</p>
      <p className="worktree-preparation-line">{worktreeCreationMessage(worktree.reason, worktree.message)}</p>
      <div className="worktree-preparation-actions">
        <Button variant="minimal" size="small" text={t("Close")} onClick={onClose} />
        <Button intent="primary" size="small" icon={<AppIcon name="refresh" size={14} />} text={t("Retry")} onClick={onRetry} />
      </div>
    </section>;
  }
  // The step that runs, and what git last wrote of it: a line, and a bar when git says how far it is.
  const step = [...worktree.steps].reverse().find(candidate => candidate.state === "running") ?? null;
  const branch = worktree.branch ?? worktree.steps.find(candidate => candidate.kind === "worktree")?.detail ?? null;
  const doing = step?.kind === "checkout" ? t("Checking out the files…") : step?.kind === "submodules" ? t("Updating the submodules…")
    : branch ? t("Creating the worktree {branch}…", { branch }) : t("Creating the worktree…");
  const line = step && step.kind !== "worktree" ? step.detail : null;
  const percent = progressPercent(line);
  const elapsed = worktree.steps.reduce((total, candidate) => total + candidate.elapsedMilliseconds, 0);
  return <section className="worktree-preparation" aria-live="polite" data-state="preparing">
    <p className="worktree-preparation-title"><ActivitySpinner size={13} />
      {branch ? t("Preparing the worktree {branch}…", { branch }) : t("Preparing the worktree…")}
      <span className="worktree-preparation-time">{stepElapsed(elapsed)}</span></p>
    <p className="worktree-preparation-line" data-kind={step?.kind ?? "worktree"}><span>{doing}</span>{line && <small title={line}>{line}</small>}</p>
    {percent !== null && <div className="worktree-preparation-bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
      <span style={{ width: `${percent}%` }} /></div>}
    <p className="worktree-preparation-hint">{t("Write the prompt meanwhile: the session starts once the worktree is ready.")}</p>
  </section>;
}
