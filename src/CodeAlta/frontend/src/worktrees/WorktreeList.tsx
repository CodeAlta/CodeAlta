import { useState } from "react";
import { Alert, Button, Callout } from "@blueprintjs/core";
import { worktrees as worktreesApi } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { sameFolder, worktreeFailure, type Worktree } from "./worktrees";

export type WorktreeApi = Pick<typeof worktreesApi, "remove">;

/**
 * The checkouts of a project's repository in the changes of the project: the folder of the project, then its
 * worktrees. A row shows the changes and the commits of its checkout; a worktree is removed from its row,
 * unless a session is working in it.
 */
export function WorktreeList({ epoch, projectId, projectName, worktrees, selected, sessions, onSelect, onRemoved, api = worktreesApi }: {
  epoch: string; projectId: string; projectName: string;
  worktrees: readonly Worktree[];
  /** The folder of the checkout that is shown; null for the folder of the project. */
  selected: string | null;
  /** How many sessions work in a checkout. */
  sessions: (worktree: Worktree) => number;
  onSelect: (worktree: Worktree) => void;
  /** A worktree is gone: the list is read again. */
  onRemoved: (worktree: Worktree) => void;
  api?: WorktreeApi;
}) {
  const { t } = useShellLanguage();
  const [asking, setAsking] = useState<{ worktree: Worktree; force: boolean } | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ text: string; intent: "primary" | "danger" } | null>(null);

  function remove(worktree: Worktree, force: boolean) {
    setRemoving(worktree.path); setNotice(null);
    void api.remove({ expectedEpoch: epoch, projectId, path: worktree.path, force }, { timeoutMilliseconds: 600_000 })
      .then(reply => {
        setRemoving(null);
        if (reply?.status === "ok") {
          if (reply.branchKept) setNotice({ intent: "primary", text: t("The branch {branch} is kept: it holds commits of its own.", { branch: reply.branchKept }) });
          onRemoved(worktree);
          // What is not committed is lost with the folder: that is asked for by name.
        } else if (reply?.status === "dirty" && !force) setAsking({ worktree, force: true });
        else setNotice({ intent: "danger", text: worktreeFailure(reply?.status ?? "failed", reply?.message, t) });
      }, () => { setRemoving(null); setNotice({ intent: "danger", text: t("Git could not do it.") }); });
  }

  return <section className="worktree-list" aria-label={t("Worktrees")}>
    <h3>{t("Worktrees")}</h3>
    {notice && <Callout intent={notice.intent} compact className="worktree-notice" onClick={() => setNotice(null)}>{notice.text}</Callout>}
    <div className="worktree-rows" role="listbox" aria-label={t("Worktrees")}>
      {worktrees.map(worktree => {
        const count = sessions(worktree);
        const shown = worktree.main ? selected === null : sameFolder(selected, worktree.folder);
        return <div className="worktree-row" key={worktree.path} data-main={worktree.main || undefined} data-missing={worktree.missing || undefined}>
          <button type="button" role="option" aria-selected={shown} disabled={worktree.missing} onClick={() => onSelect(worktree)}
            title={`${worktree.path}${worktree.branch ? `\n${t("Branch {branch}", { branch: worktree.branch })}` : ""}${worktree.missing ? `\n${t("The folder is gone.")}` : ""}`}>
            <span className="worktree-row-icon"><AppIcon name={worktree.main ? "folder" : "worktree"} size={14} /></span>
            <span className="worktree-row-text">
              <strong>{worktree.main ? projectName : worktree.name}</strong>
              <small>
                {worktree.missing ? <span>{t("Folder gone")}</span>
                  : <span className="worktree-row-branch"><AppIcon name="branch" size={11} />{worktree.branch ?? worktree.head ?? ""}</span>}
                {count > 0 && <span>{t(count === 1 ? "{count} session" : "{count} sessions", { count })}</span>}
              </small>
            </span>
            {worktree.busy && <span className="worktree-row-busy" title={t("A session is working here")}><ActivitySpinner size={12} /></span>}
          </button>
          {!worktree.main && <Button variant="minimal" size="small" className="worktree-row-remove" disabled={worktree.busy || removing !== null}
            icon={removing === worktree.path ? <ActivitySpinner size={14} /> : <AppIcon name="trash" size={14} />}
            aria-label={t("Remove the worktree {name}", { name: worktree.name })}
            title={worktree.busy ? t("A session is working here") : t("Remove the worktree {name}", { name: worktree.name })}
            onClick={() => setAsking({ worktree, force: false })} />}
        </div>;
      })}
    </div>
    <Alert isOpen={!!asking} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t(asking?.force ? "Remove with its changes" : "Remove")}
      canEscapeKeyCancel canOutsideClickCancel onCancel={() => setAsking(null)}
      onConfirm={() => { const asked = asking; setAsking(null); if (asked) remove(asked.worktree, asked.force); }}>
      <p>{asking?.force
        ? t("“{name}” holds changes that are not committed. Remove it with them?", { name: asking.worktree.name })
        : t("Remove the worktree “{name}”? Its folder is deleted, and its sessions continue in the folder of the project.", { name: asking?.worktree.name ?? "" })}</p>
      {asking && <p className="worktree-alert-path"><code>{asking.worktree.path}</code></p>}
    </Alert>
  </section>;
}
