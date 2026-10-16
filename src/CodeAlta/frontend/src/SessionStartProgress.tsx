import { Button } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

// The ellipsis of a step that runs, written by its code point: a page read without a declared charset must still match it.
const ellipsis = String.fromCharCode(0x2026);

/** A step of the creation of a session, as the host says it: the worktree, its files, its submodules, the session, ready. */
export type SessionStartStep = Readonly<{ kind: string; state: string; detail: string | null; elapsedMilliseconds: number; message: string | null }>;

/** How long a step took, or has taken so far: tenths of a second at first, then seconds, then minutes. */
export function stepElapsed(milliseconds: number): string {
  const seconds = Math.max(0, milliseconds) / 1000;
  if (seconds < 10) return `${seconds.toFixed(1)} s`;
  if (seconds < 60) return `${Math.floor(seconds)} s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes} min ${String(Math.floor(seconds % 60)).padStart(2, "0")} s`;
}

/**
 * The steps a session goes through while the host creates it, in the tab it starts in: a spinner on the one that
 * runs, a check mark on those that are done, each with how long it took. A step that failed says what git or the
 * host said, with Retry and Close.
 */
export function SessionStartProgress({ steps, error, onRetry, onClose }: {
  steps: readonly SessionStartStep[];
  /** Why the session was not created; the creation still runs while null. */
  error: string | null;
  onRetry?: () => void; onClose?: () => void;
}) {
  const { t } = useShellLanguage();
  // A step that failed is named as when it ran, without the ellipsis.
  function label(step: SessionStartStep) {
    const text = phrase(step);
    return step.state === "failed" && text.endsWith(ellipsis) ? text.slice(0, -ellipsis.length) : text;
  }
  function phrase(step: SessionStartStep) {
    const running = step.state !== "done";
    switch (step.kind) {
      case "worktree": return step.detail ? t(running ? "Creating the worktree {branch}…" : "Created the worktree {branch}", { branch: step.detail })
        : t(running ? "Creating the worktree…" : "Created the worktree");
      case "checkout": return t(running ? "Checking out the files…" : "Checked out the files");
      case "submodules": return t(running ? "Updating the submodules…" : "Updated the submodules");
      case "session": return t(running ? "Preparing the session…" : "Prepared the session");
      case "ready": return t("Ready");
      default: return step.kind;
    }
  }
  // What runs below the label: the command of git, or the progress git gives.
  function detail(step: SessionStartStep) {
    if (step.kind === "worktree") return "git worktree add";
    if (step.kind === "checkout") return step.state === "running" && step.detail ? step.detail : "git checkout";
    if (step.kind === "submodules") return step.state === "running" && step.detail ? step.detail : "git submodule update";
    return null;
  }
  const failed = steps.find(step => step.state === "failed");
  return <section className={`session-start${error ? " failed" : ""}`} aria-label={t("Starting the session")} aria-live="polite">
    <ol className="session-start-steps">
      {steps.map((step, index) => <li key={`${step.kind}:${index}`} className={`session-start-step ${step.state}`} data-kind={step.kind} data-state={step.state}>
        <span className="session-start-mark">{step.state === "running" ? <ActivitySpinner size={13} />
          : <AppIcon name={step.state === "failed" ? "error" : "check"} size={14} />}</span>
        <span className="session-start-label">{label(step)}{detail(step) && <small>{detail(step)}</small>}</span>
        {step.kind !== "ready" && <span className="session-start-time">{stepElapsed(step.elapsedMilliseconds)}</span>}
      </li>)}
      {!steps.length && !error && <li className="session-start-step running" data-state="running">
        <span className="session-start-mark"><ActivitySpinner size={13} /></span>
        <span className="session-start-label">{t("Starting the session…")}</span>
      </li>}
    </ol>
    {error && <div className="session-start-error" role="alert">
      <p>{error}</p>
      {failed?.message && failed.message !== error && <pre>{failed.message}</pre>}
      <div className="session-start-actions">
        {onClose && <Button variant="minimal" size="small" text={t("Close")} onClick={onClose} />}
        {onRetry && <Button intent="primary" size="small" icon={<AppIcon name="refresh" size={14} />} text={t("Retry")} onClick={onRetry} />}
      </div>
    </div>}
  </section>;
}
