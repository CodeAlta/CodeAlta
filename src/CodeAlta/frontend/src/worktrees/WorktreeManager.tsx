import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Button, Callout, Checkbox, NonIdealState } from "@blueprintjs/core";
import { worktrees as worktreesApi } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { editorFailure, inventoryFailure, inventoryGroups, inventoryReply, keepSelection, lastUse, mergeOutcomes, removable, removalChunks, removalChunkSize, removalCounts, removalReason,
  removalReply, removalUnknown, selectedRows, showsChanges, toggleAll, toggleOne, type Inventory, type InventoryRow, type RemovalOutcome } from "./worktreeInventory";

export type WorktreeManagerApi = Pick<typeof worktreesApi, "inventory" | "removeMany" | "openEditor">;

const refreshMilliseconds = 10_000;
// A refusal that asking again does not lift: the list it replaces is no longer shown.
const lasting = new Set(["not_repository", "unknown_project", "project_unavailable", "unavailable", "stale_epoch"]);

type Phase =
  | Readonly<{ kind: "list" }>
  /** The worktrees that were chosen, shown again before anything is removed. */
  | Readonly<{ kind: "review"; rows: readonly InventoryRow[] }>
  /** The worktrees that hold changes that are not committed: throwing those away is asked by itself. */
  | Readonly<{ kind: "discard"; rows: readonly InventoryRow[] }>
  | Readonly<{ kind: "removing"; total: number; done: number }>
  | Readonly<{ kind: "results" }>;

/** The name, the folder and the branch of a checkout. */
function Checkout({ row, projectName }: { row: InventoryRow; projectName: string }) {
  const { t } = useShellLanguage();
  return <span className="worktree-checkout" data-missing={row.missing || undefined} data-main={row.main || undefined}>
    <span className="worktree-checkout-icon"><AppIcon name={row.main ? "folder" : "worktree"} size={15} /></span>
    <span className="worktree-checkout-text">
      <strong>{row.project ? projectName : row.name}</strong>
      <code title={row.path}>{row.path}</code>
    </span>
    <span className="worktree-checkout-branch" title={row.branch ? t("Branch {branch}", { branch: row.branch }) : row.head ? t("Detached at {branch}", { branch: row.head }) : undefined}>
      <AppIcon name="branch" size={12} />{row.branch ?? row.head ?? "—"}</span>
  </span>;
}

/** What protects a checkout, and whether its folder is there. */
function States({ row }: { row: InventoryRow }) {
  const { t } = useShellLanguage();
  const running = row.sessions.filter(session => session.running).map(session => session.title);
  return <span className="worktree-states">
    {row.project ? <span className="worktree-state" data-state="main" title={t("This is the folder of the project: it is not a worktree to remove.")}>{t("Project folder")}</span>
      : row.main && <span className="worktree-state" data-state="main" title={t("The checkout the repository lives in is not removed.")}>{t("Main checkout")}</span>}
    {row.busy && <span className="worktree-state" data-state="busy" title={running.length ? `${t("A session is working here")}\n${running.join("\n")}` : t("A session is working here")}>
      <ActivitySpinner size={11} />{t("In use")}</span>}
    {row.locked && <span className="worktree-state" data-state="locked" title={t("Git keeps this worktree locked: unlock it with git to remove it.")}>{t("Locked")}</span>}
    {row.missing && <span className="worktree-state" data-state="missing" title={t("Git still lists this worktree, and its folder is gone.")}>{t("Folder gone")}</span>}
  </span>;
}

/** When a checkout was last used, by what the sessions of the catalog record. */
function LastUse({ row, sessionsKnown }: { row: InventoryRow; sessionsKnown: boolean }) {
  const { t, locale } = useShellLanguage();
  const used = lastUse(row);
  if (!used) return <span className="worktree-use" data-unknown="true"
    title={t(sessionsKnown ? "No session records this worktree: when it was last used is not known." : "The sessions could not be read.")}>{t("Unknown")}</span>;
  const at = sessionTime(used.at, locale);
  const titles = row.sessions.map(session => `${session.running ? `${t("Running")} · ` : ""}${session.title}`).join("\n");
  return <span className="worktree-use" title={`${at.title}${titles ? `\n${titles}` : ""}`}>
    <time dateTime={at.dateTime}>{at.label}</time>
    {used.session && <small>{used.session.title}</small>}
    {row.sessionCount > 1 && <small>{t("{count} sessions", { count: row.sessionCount })}</small>}
  </span>;
}

/**
 * The checkouts of a project's repository: every one git lists, on disk or not, with what protects it, when it
 * was last used and by which session. One, several or all the worktrees that can go are selected and removed
 * from here, and a checkout is opened in the code editor or in the changes of the project.
 */
export function WorktreeTable({ inventory, projectName, selected, busy, onToggle, onToggleAll, onRemove, onOpenEditor, onShowChanges }: {
  inventory: Inventory; projectName: string; selected: ReadonlySet<string>;
  /** Something is being done: nothing else is started. */
  busy: boolean;
  onToggle: (row: InventoryRow) => void; onToggleAll: () => void;
  onRemove: (row: InventoryRow) => void; onOpenEditor: (row: InventoryRow) => void; onShowChanges: (row: InventoryRow) => void;
}) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState<string | null>(null);
  const groups = inventoryGroups(inventory.rows);
  const candidates = inventory.rows.filter(removable);
  const all = candidates.length > 0 && candidates.every(row => selected.has(row.path));
  const copy = (row: InventoryRow) => void navigator.clipboard.writeText(row.path).then(() => { setCopied(row.path); window.setTimeout(() => setCopied(null), 1400); }, () => { });
  const line = (row: InventoryRow) => {
    const can = removable(row);
    const why = row.protection === "in_use" ? t("A session is working here") : row.protection === "locked" ? t("Git keeps this worktree locked: unlock it with git to remove it.")
      : row.protection === "main" || !can ? t(row.project ? "This is the folder of the project: it is not a worktree to remove." : "The checkout the repository lives in is not removed.") : null;
    return <tr key={row.path} className="worktree-line" data-path={row.path} data-removable={can || undefined} data-missing={row.missing || undefined} data-selected={selected.has(row.path) || undefined}>
      <td className="worktree-cell-select" title={why ?? undefined}><Checkbox checked={can && selected.has(row.path)} disabled={!can || busy}
        aria-label={can ? t("Select the worktree {name}", { name: row.name }) : `${row.name}: ${why}`} onChange={() => onToggle(row)} /></td>
      <td className="worktree-cell-checkout"><Checkout row={row} projectName={projectName} />
        {/* Where the window is too narrow for the column of the states, they are under the name. */}
        <span className="worktree-states-inline"><States row={row} /></span></td>
      <td className="worktree-cell-states"><States row={row} /></td>
      <td className="worktree-cell-use"><LastUse row={row} sessionsKnown={inventory.sessionsKnown} /></td>
      <td className="worktree-cell-actions">
        <Button variant="minimal" size="small" icon={<AppIcon name="code" size={14} />} disabled={row.missing || busy}
          aria-label={t("Open {name} in the code editor", { name: row.name })} title={t("Open in the code editor")} onClick={() => onOpenEditor(row)} />
        <Button variant="minimal" size="small" icon={<AppIcon name="changes" size={14} />} disabled={!showsChanges(row) || busy}
          aria-label={t("Show the changes of {name}", { name: row.name })} title={t("Show changes")} onClick={() => onShowChanges(row)} />
        <Button variant="minimal" size="small" icon={<AppIcon name={copied === row.path ? "check" : "copy"} size={14} />}
          aria-label={t("Copy the path of {name}", { name: row.name })} title={t("Copy path")} onClick={() => copy(row)} />
        <Button variant="minimal" size="small" icon={<AppIcon name="trash" size={14} />} disabled={!can || busy}
          aria-label={t("Remove the worktree {name}", { name: row.name })} title={why ?? t("Remove the worktree {name}", { name: row.name })} onClick={() => onRemove(row)} />
      </td>
    </tr>;
  };
  return <table className="worktree-table">
    <thead><tr>
      <th className="worktree-cell-select"><Checkbox checked={all} indeterminate={!all && selected.size > 0} disabled={!candidates.length || busy}
        aria-label={t("Select every worktree that can be removed")} title={t("Select every worktree that can be removed")} onChange={onToggleAll} /></th>
      <th className="worktree-cell-checkout">{t("Worktree")}</th><th className="worktree-cell-states">{t("State")}</th><th className="worktree-cell-use">{t("Last used")}</th>
      <th className="worktree-cell-actions"><span className="sr-only">{t("Actions")}</span></th>
    </tr></thead>
    <tbody data-group="present">
      <tr className="worktree-group"><th colSpan={5} scope="rowgroup">{t("On disk")}<span className="worktree-group-count">{groups.present.length}</span></th></tr>
      {groups.present.map(line)}
    </tbody>
    {groups.gone.length > 0 && <tbody data-group="gone">
      <tr className="worktree-group"><th colSpan={5} scope="rowgroup" title={t("Git still lists these worktrees, and their folders are gone. Removing one only makes git forget it.")}>
        {t("Listed by git, folder gone")}<span className="worktree-group-count">{groups.gone.length}</span></th></tr>
      {groups.gone.map(line)}
    </tbody>}
    {inventory.truncated && <tfoot><tr><td colSpan={5} className="worktree-more">{t("Git lists more worktrees than this window shows.")}</td></tr></tfoot>}
  </table>;
}

/** The worktrees that were chosen, shown again with what removing them does, before anything is removed. */
export function WorktreeRemovalReview({ rows, projectName, deleteBranches, onDeleteBranches, onCancel, onConfirm }: {
  rows: readonly InventoryRow[]; projectName: string; deleteBranches: boolean; onDeleteBranches: (value: boolean) => void; onCancel: () => void; onConfirm: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const sessions = rows.reduce((count, row) => count + row.sessionCount, 0), gone = rows.filter(row => row.missing).length;
  // What is not committed keeps a worktree; what git ignores does not, and goes with a folder that is there.
  const folders = rows.some(row => !row.missing);
  return <section className="worktree-review" aria-label={t("Review before removing")}>
    <h3>{t(rows.length === 1 ? "Remove this worktree?" : "Remove these {count} worktrees?", { count: rows.length })}</h3>
    <p>{t("Their folders are deleted. A worktree that holds changes that are not committed is not removed: it is listed afterwards, and asked about by itself.")}</p>
    {folders && <p className="worktree-review-ignored">{t("Files that git ignores are deleted with the folder.")}</p>}
    {sessions > 0 && <p>{t(sessions === 1 ? "{count} session recorded them: it continues in the folder of the project." : "{count} sessions recorded them: they continue in the folder of the project.", { count: sessions })}</p>}
    {gone > 0 && <p>{t(gone === 1 ? "{count} has no folder any more: git only forgets it." : "{count} have no folder any more: git only forgets them.", { count: gone })}</p>}
    <ul className="worktree-review-rows">{rows.map(row => {
      const used = lastUse(row);
      return <li key={row.path} data-path={row.path}><Checkout row={row} projectName={projectName} /><States row={row} />
        <span className="worktree-review-use">{used ? t("Last used {time}", { time: sessionTime(used.at, locale).label }) : t("Last use unknown")}</span></li>;
    })}</ul>
    <Checkbox className="worktree-review-branches" checked={deleteBranches} onChange={event => onDeleteBranches(event.currentTarget.checked)}
      label={t("Also delete their alta/ branches whose commits are all in another branch")} />
    <p className="worktree-review-note">{t(deleteBranches ? "A branch that holds commits of its own is kept." : "Every branch is kept.")}</p>
    <footer><Button text={t("Back")} onClick={onCancel} autoFocus />
      <Button intent="danger" icon={<AppIcon name="trash" size={14} />} text={t(rows.length === 1 ? "Remove the worktree" : "Remove {count} worktrees", { count: rows.length })} onClick={onConfirm} /></footer>
  </section>;
}

/** What is asked by itself: to remove worktrees with their changes that are not committed. */
export function WorktreeDiscardReview({ rows, projectName, onCancel, onConfirm }: { rows: readonly InventoryRow[]; projectName: string; onCancel: () => void; onConfirm: () => void }) {
  const { t } = useShellLanguage();
  return <section className="worktree-review" data-discard="true" aria-label={t("Discard changes that are not committed")}>
    <h3>{t(rows.length === 1 ? "Remove this worktree with its changes?" : "Remove these {count} worktrees with their changes?", { count: rows.length })}</h3>
    <Callout intent="danger" compact>{t(rows.length === 1 ? "What is not committed in it is deleted with its folder. It cannot be recovered."
      : "What is not committed in them is deleted with their folders. It cannot be recovered.")}</Callout>
    <ul className="worktree-review-rows">{rows.map(row => <li key={row.path} data-path={row.path}><Checkout row={row} projectName={projectName} /></li>)}</ul>
    <footer><Button text={t("Back")} onClick={onCancel} autoFocus />
      <Button intent="danger" icon={<AppIcon name="trash" size={14} />} onClick={onConfirm}
        text={t(rows.length === 1 ? "Discard its changes and remove it" : "Discard their changes and remove {count}", { count: rows.length })} /></footer>
  </section>;
}

/** What became of each worktree that was asked to go, separating refusals from outcomes the host did not confirm. */
export function WorktreeRemovalResults({ outcomes, projectName, onDiscard, onDone }: {
  outcomes: readonly RemovalOutcome[]; projectName: string;
  /** Asks about the worktrees that stay because they hold changes that are not committed. */
  onDiscard: (rows: readonly InventoryRow[]) => void; onDone: () => void;
}) {
  const { t } = useShellLanguage();
  const counts = removalCounts(outcomes);
  const dirty = outcomes.filter(outcome => outcome.status === "dirty").map(outcome => outcome.row);
  return <section className="worktree-results" aria-label={t("What was removed")}>
    <h3 role="status">{counts.removed === outcomes.length ? t(counts.removed === 1 ? "The worktree was removed." : "{count} worktrees were removed.", { count: counts.removed })
      : t(counts.unknown ? "{removed} of {count} removed, {kept} not removed, {unknown} unknown." : "{removed} of {count} removed, {kept} not removed.",
        { removed: counts.removed, count: outcomes.length, kept: counts.dirty + counts.kept, unknown: counts.unknown })}</h3>
    <ul className="worktree-results-rows">{outcomes.map(outcome => {
      const done = outcome.status === "ok";
      return <li key={outcome.row.path} data-path={outcome.row.path} data-status={outcome.status}>
        <span className="worktree-result-mark"><AppIcon name={done ? "check" : outcome.status === "dirty" ? "warning" : "error"} size={15} /></span>
        <Checkout row={outcome.row} projectName={projectName} />
        <span className="worktree-result-text">
          <strong>{done ? t(outcome.row.missing ? "Forgotten by git" : "Removed") : t(removalUnknown(outcome.status) ? "Outcome unknown" : "Not removed")}</strong>
          {!done && <span>{removalReason(outcome.status, outcome.message, t)}</span>}
          {outcome.branchDeleted && <span>{t("The branch {branch} was deleted.", { branch: outcome.branchDeleted })}</span>}
          {outcome.branchKept && <span>{t("The branch {branch} is kept: it holds commits of its own.", { branch: outcome.branchKept })}</span>}
        </span>
      </li>;
    })}</ul>
    {dirty.length > 0 && <Callout intent="warning" compact className="worktree-results-dirty">
      <span>{t(dirty.length === 1 ? "{count} worktree holds changes that are not committed. It was not removed." : "{count} worktrees hold changes that are not committed. They were not removed.", { count: dirty.length })}</span>
      <Button size="small" intent="danger" variant="outlined" text={`${t(dirty.length === 1 ? "Remove it with its changes" : "Remove them with their changes")}…`} onClick={() => onDiscard(dirty)} />
    </Callout>}
    <footer><span /><Button intent="primary" text={t("Done")} onClick={onDone} autoFocus /></footer>
  </section>;
}

/**
 * The window of the worktrees of a project. It reads the checkouts when it opens, when the window of the
 * application comes back to the front, on demand and every ten seconds while the list is shown. Automatic reads
 * coalesce while a reply is pending; an explicit newer question supersedes an older answer. Removing asks first,
 * with the worktrees named; the ones that hold changes that are not committed stay, and throwing those changes
 * away is asked by itself.
 */
export function WorktreeManager({ epoch, project, onClose, onShowChanges, onChanged, api = worktreesApi }: {
  epoch: string; project: Readonly<{ id: string; name: string; path: string }>;
  onClose: () => void;
  /** Shows the changes of a checkout in the changes tab of the project; the window closes. */
  onShowChanges: (row: InventoryRow) => void;
  /** Worktrees were removed: what the sessions record is read again. */
  onChanged?: () => void;
  api?: WorktreeManagerApi;
}) {
  const { t } = useShellLanguage();
  const titleId = useId();
  const [inventory, setInventory] = useState<Inventory | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [reading, setReading] = useState(true);
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set());
  const [phase, setPhase] = useState<Phase>({ kind: "list" });
  const [deleteBranches, setDeleteBranches] = useState(false);
  const [outcomes, setOutcomes] = useState<readonly RemovalOutcome[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [opening, setOpening] = useState(false);
  // The last question asked: an answer to an older one says what was, not what is.
  const asked = useRef(0);
  const pending = useRef<number | null>(null);
  const alive = useRef(false);
  const stop = useRef(false);
  const changed = useRef(onChanged); changed.current = onChanged;

  const refresh = useCallback((shown = false) => {
    // A slow host must be allowed to answer: timer/focus events neither supersede nor queue another read.
    if (!shown && pending.current !== null) return;
    const ticket = ++asked.current;
    pending.current = ticket;
    if (shown) setReading(true);
    void api.inventory({ expectedEpoch: epoch, projectId: project.id }, { timeoutMilliseconds: 30_000 })
      .then(reply => inventoryReply(reply, project.id), () => "read_failed")
      .then(value => {
        if (pending.current === ticket) pending.current = null;
        if (!alive.current || ticket !== asked.current) return;
        setReading(false);
        if (typeof value === "string") {
          setFailure(value);
          if (lasting.has(value)) { setInventory(null); setSelected(new Set()); }
          return;
        }
        setFailure(null); setInventory(value);
        setSelected(current => keepSelection(current, value.rows));
      });
  }, [api, epoch, project.id]);

  useEffect(() => {
    alive.current = true;
    refresh(true);
    return () => { alive.current = false; stop.current = true; asked.current++; pending.current = null; };
  }, [refresh]);
  const listed = phase.kind === "list";
  useEffect(() => {
    if (!listed) return;
    const focused = () => refresh();
    window.addEventListener("focus", focused);
    const timer = window.setInterval(() => { if (document.visibilityState !== "hidden") refresh(); }, refreshMilliseconds);
    return () => { window.removeEventListener("focus", focused); window.clearInterval(timer); };
  }, [listed, refresh]);

  // Removes worktrees, a few in each request, and keeps what the host said of each. `discard` is only true for
  // the worktrees the user was asked about by themselves.
  async function remove(rows: readonly InventoryRow[], discard: boolean) {
    stop.current = false;
    setNotice(null); setPhase({ kind: "removing", total: rows.length, done: 0 });
    const collected: RemovalOutcome[] = [];
    const none = { message: null, branchKept: null, branchDeleted: null };
    for (const chunk of removalChunks(rows)) {
      if (stop.current) { collected.push(...chunk.map(row => ({ row, path: row.path, status: "canceled", ...none }))); continue; }
      const paths = chunk.map(row => row.path);
      const reply = await api.removeMany({ expectedEpoch: epoch, projectId: project.id, paths, discard: discard ? paths : [], deleteMergedBranches: deleteBranches },
        { timeoutMilliseconds: 600_000 }).then(value => removalReply(value, paths), () => "unconfirmed");
      if (typeof reply === "string") {
        // Nothing is known of these, and nothing more is asked of a host that did not answer.
        collected.push(...chunk.map(row => ({ row, path: row.path, status: reply, ...none })));
        stop.current = true;
      } else collected.push(...reply.map((result, index) => ({ ...result, row: chunk[index] })));
      if (!alive.current) return;
      setPhase({ kind: "removing", total: rows.length, done: collected.length });
    }
    if (!alive.current) return;
    setOutcomes(known => discard ? mergeOutcomes(known, collected) : collected);
    setSelected(new Set()); setPhase({ kind: "results" });
    refresh(true);
    changed.current?.();
  }

  function openEditor(row: InventoryRow) {
    setOpening(true); setNotice(null);
    void api.openEditor({ expectedEpoch: epoch, projectId: project.id, path: row.path }, { timeoutMilliseconds: 30_000 })
      .then(reply => typeof reply?.status === "string" ? reply.status : "failed", () => "failed")
      .then(status => {
        if (!alive.current) return;
        setOpening(false);
        if (status === "ok") onClose();
        else { setNotice(`${row.name}: ${editorFailure(status, t)}`); refresh(); }
      });
  }

  const working = phase.kind === "removing";
  // Escape, and a press beside the window while it only lists: one step back, never out of a removal.
  function back() {
    if (working) return;
    if (phase.kind === "list") onClose();
    else if (phase.kind === "discard") setPhase({ kind: "results" });
    else { setPhase({ kind: "list" }); setOutcomes([]); }
  }
  const chosen = inventory ? selectedRows(selected, inventory.rows) : [];
  // `main` protects both checkouts when the project lives in a linked worktree; only the repository's main one is excluded.
  const main = inventory?.rows.find(row => row.main && !row.project) ?? inventory?.rows.find(row => row.project);
  const total = (inventory?.rows.length ?? 0) - (main ? 1 : 0);

  return <AppWindow storageKey="codealta.desktop.window.worktrees.v1" className="worktree-manager-dialog" titleId={titleId}
    title={<>{t("Worktrees")}<span className="worktree-manager-project">{project.name}</span></>}
    preferredSize={viewport => ({ width: Math.min(1040, viewport.width - 40), height: Math.min(720, viewport.height - 40) })} minimumSize={{ width: 520, height: 360 }}
    onClose={() => { if (!working) onClose(); }} closeLabel={t("Close")} keepOnOutsidePress={!listed}
    onCancel={event => { event.preventDefault(); back(); }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); back(); } }}>
    <div className="worktree-manager" data-phase={phase.kind}>
      {phase.kind === "list" && <>
        <header className="worktree-manager-bar">
          <span className="worktree-manager-count" role="status">{inventory
            ? t(total === 1 ? "{count} worktree" : "{count} worktrees", { count: total }) : ""}</span>
          <span className="worktree-manager-spacer" />
          <Button variant="minimal" size="small" icon={reading ? <ActivitySpinner size={14} /> : <AppIcon name="refresh" size={14} />}
            aria-label={t("Refresh")} title={t("Refresh")} onClick={() => refresh(true)} />
        </header>
        {notice && <Callout intent="danger" compact className="worktree-manager-notice" role="alert">{notice}</Callout>}
        {failure && inventory && <Callout intent="warning" compact className="worktree-manager-notice" role="alert">{t(inventoryFailure(failure))}</Callout>}
        {!inventory ? <NonIdealState className="worktree-manager-empty" icon={failure ? <AppIcon name="worktree" size={36} /> : <ActivitySpinner size={28} />}
            title={failure ? t("No worktrees to show") : t("Loading…")} description={failure ? t(inventoryFailure(failure)) : undefined}
            action={failure ? <Button icon={<AppIcon name="refresh" size={15} />} onClick={() => refresh(true)}>{t("Refresh")}</Button> : undefined} />
          : <div className="worktree-manager-rows">
            <WorktreeTable inventory={inventory} projectName={project.name} selected={selected} busy={opening}
              onToggle={row => setSelected(current => toggleOne(current, row))} onToggleAll={() => setSelected(current => toggleAll(current, inventory.rows))}
              onRemove={row => { setDeleteBranches(false); setPhase({ kind: "review", rows: [row] }); }} onOpenEditor={openEditor} onShowChanges={onShowChanges} />
            {total === 0 && <p className="worktree-manager-none">{t("This repository has no worktree. A session started in a new worktree makes one.")}</p>}
          </div>}
        <footer className="worktree-manager-footer">
          <span role="status">{chosen.length > 0 ? t("{count} selected", { count: chosen.length }) : ""}</span>
          <span>
            {chosen.length > 0 && <Button variant="minimal" text={t("Clear selection")} onClick={() => setSelected(new Set())} />}
            <Button intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={!chosen.length || opening}
              text={`${t(chosen.length === 0 ? "Remove" : chosen.length === 1 ? "Remove the worktree" : "Remove {count} worktrees", { count: chosen.length })}…`}
              onClick={() => { setDeleteBranches(false); setPhase({ kind: "review", rows: chosen }); }} />
          </span>
        </footer>
      </>}
      {phase.kind === "review" && <WorktreeRemovalReview rows={phase.rows} projectName={project.name} deleteBranches={deleteBranches} onDeleteBranches={setDeleteBranches}
        onCancel={back} onConfirm={() => void remove(phase.rows, false)} />}
      {phase.kind === "discard" && <WorktreeDiscardReview rows={phase.rows} projectName={project.name} onCancel={back} onConfirm={() => void remove(phase.rows, true)} />}
      {phase.kind === "removing" && <NonIdealState className="worktree-manager-empty" icon={<ActivitySpinner size={28} />}
        title={t("Removing… {done} of {count} done", { done: phase.done, count: phase.total })}
        action={phase.total > removalChunkSize ? <Button text={t("Stop")} title={t("What is being removed is finished first.")} onClick={() => { stop.current = true; }} /> : undefined} />}
      {phase.kind === "results" && <WorktreeRemovalResults outcomes={outcomes} projectName={project.name} onDiscard={rows => setPhase({ kind: "discard", rows })} onDone={back} />}
    </div>
  </AppWindow>;
}
