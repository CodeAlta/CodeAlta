import { useEffect, useId, useState } from "react";
import { Button, HTMLSelect } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { folderTargets, sessionFolderMessage, type FolderSwitchResult, type FolderTarget } from "./sessionFolder";
import type { WorkPlaceApi } from "./WorkPlaceChoice";
import { branchesReply, worktreesReply, type Branch, type Worktree } from "./worktrees";

/**
 * Sends a session that exists to work elsewhere in the repository of its project: the folder of the project, another
 * worktree (wherever git keeps it), or a new worktree. Part of the Workspace popover of the session. The session keeps
 * its conversation, and its next prompt tells its agent where it works; no file moves.
 */
export function SessionFolderChoice({ epoch, projectId, current, branch, running, api, onSwitch, onClose, onSwitched = onClose }: {
  epoch: string; projectId: string;
  /** The worktree the session records, null for the folder of the project. */
  current: string | null;
  /** The branch the folder of the project is on: what a new worktree starts from by default. */
  branch: string | null;
  /** A run of the session is active: it changes folder once it is idle. */
  running: boolean;
  api: WorkPlaceApi;
  onSwitch: (target: FolderTarget) => Promise<FolderSwitchResult>;
  onClose: () => void;
  /** The session went to work in the folder chosen; the choice is closed when it is not given. */
  onSwitched?: () => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [worktrees, setWorktrees] = useState<readonly Worktree[] | null>(null);
  const [branches, setBranches] = useState<readonly Branch[]>([]);
  // `project`, `new`, or the folder of a checkout.
  const [choice, setChoice] = useState<string>("");
  const [base, setBase] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    void api.list({ expectedEpoch: epoch, projectId }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => worktreesReply(reply, projectId), () => "read_failed")
      .then(value => { if (!abort.signal.aborted) setWorktrees(typeof value === "string" ? [] : folderTargets(value.worktrees, current)); });
    void api.branches({ expectedEpoch: epoch, projectId, worktree: null }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(branchesReply, () => "read_failed").then(value => { if (!abort.signal.aborted) setBranches(typeof value === "string" ? [] : value.branches); });
    return () => abort.abort();
  }, [epoch, projectId, current, api]);

  const options = [
    { value: "", label: worktrees === null ? "…" : t("Choose where the session works"), disabled: true },
    ...(worktrees ?? []).map(worktree => worktree.project
      ? { value: "project", label: t("Project folder") }
      : { value: worktree.folder, label: worktree.branch ? `${worktree.name} (${worktree.branch})` : worktree.name }),
    { value: "new", label: t("New worktree") },
  ];
  const target: FolderTarget | null = choice === "project" ? { kind: "project" } : choice === "new" ? { kind: "new", base } : choice ? { kind: "worktree", folder: choice } : null;
  const switchNow = async () => {
    if (!target || busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await onSwitch(target);
      if (result.kind === "switched") onSwitched();
      else setError([t(sessionFolderMessage(result.code)), result.message].filter(Boolean).join(" "));
    } finally { setBusy(false); }
  };

  return <div className="session-folder-choice" role="group" aria-label={t("Change worktree")}>
    <div className="work-place-row">
      <label htmlFor={`${id}-folder`}>{t("Work in")}</label>
      <HTMLSelect id={`${id}-folder`} value={choice} disabled={busy} options={options} onChange={event => { setChoice(event.currentTarget.value); setError(null); }} />
    </div>
    {choice === "new" && <div className="work-place-row">
      <label htmlFor={`${id}-base`}>{t("Start from")}</label>
      <HTMLSelect id={`${id}-base`} value={base ?? ""} disabled={busy} onChange={event => setBase(event.currentTarget.value || null)}
        options={[{ value: "", label: branch ? t("Current commit ({branch})", { branch }) : t("Current commit") }, ...branches.map(value => ({ value: value.name, label: value.name }))]} />
    </div>}
    <p className="muted-text">{t("The conversation stays, and the agent is told where it works now. No file is moved: changes that are not committed stay in the folder the session leaves.")}</p>
    {running && <p className="session-folder-note" role="status"><AppIcon name="info" size={14} />{t("The session is working: its folder changes once it is idle.")}</p>}
    {error && <p className="session-folder-error" role="alert">{error}</p>}
    <footer>
      <Button size="small" variant="minimal" disabled={busy} onClick={onClose}>{t("Cancel")}</Button>
      <Button size="small" intent="primary" icon={<AppIcon name="worktree" size={14} />} disabled={!target || running} loading={busy} onClick={() => void switchNow()}>
        {t("Switch")}</Button>
    </footer>
  </div>;
}
