import { Fragment, useCallback, useEffect, useId, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Button, Callout, Checkbox, NonIdealState, PopoverNext } from "@blueprintjs/core";
import { worktrees as worktreesHostApi, type DiskUsageCheckout } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { openInFileManagerKey, useExternalOpener } from "../externalEditors/externalEditors";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { ShareDonut, ShareSwatch } from "../storage/ShareDonut";
import { storageBytes, storageSize } from "../storage/storage";
import { useWorktreeRemoval, type WorktreeManagerApi } from "../worktrees/WorktreeManager";
import { inventoryFailure, inventoryReply, removable } from "../worktrees/worktreeInventory";
import { sameFolder } from "../worktrees/worktrees";
import { DiskUsageClean } from "./DiskUsageClean";
import { checkoutsByPath, cleanBlock, cleanBlockText, drillable, levelDonut, nodePath, sizeText, sortNodes, usageTree, verdictLabel, verdictReasons,
  type SpaceName, type Sort, type SortColumn, type UsageNode } from "./diskUsage";
import type { DiskUsageHub } from "./diskUsageHub";

/** What the tab asks of the worktrees: their list, to remove one as their window does. */
export type DiskUsageWorktreesApi = Pick<WorktreeManagerApi, "inventory" | "removeMany">;

const refusals: Readonly<Record<string, "Disk usage is not available in this window." | "The host changed. Reload the window.">> = {
  unavailable: "Disk usage is not available in this window.", stale_epoch: "The host changed. Reload the window.",
};

// The plain buttons of the panel draw themselves: `settings-own-button` keeps the look Settings gives its own buttons off
// them when the panel is a view of the Storage page.
const icons: Readonly<Record<UsageNode["kind"], IconName>> = {
  root: "database", space: "space", project: "folder", worktrees: "worktree", sessions: "chat", checkout: "worktree", session: "chat",
};

/**
 * The disk usage: what the projects, their worktrees and their sessions hold, as a table of a level to
 * drill into and the donut whose legend it is. It shows the last measurement at once, with its age, and measures again
 * on demand, filling as each project is measured. From a checkout it cleans the build output, removes a worktree as the
 * window of the worktrees does, or opens it; a session opens. It is a tab of its own, and a view of the Storage page
 * (`embedded`): both show the one state of the window's hub.
 */
export function DiskUsagePanel({ epoch, spaces, hub, visible = true, worktreesApi = worktreesHostApi, embedded, onActivate, onOpenSession }: {
  /** The host epoch; null where the window has no host of its own. */
  epoch: string | null;
  spaces: readonly SpaceName[];
  /** The measurement of the window, which every view of it shows. */
  hub: DiskUsageHub;
  visible?: boolean;
  worktreesApi?: DiskUsageWorktreesApi;
  /** Shown inside another page: no title of its own, and a button that opens the tab. */
  embedded?: Readonly<{ onOpenTab: () => void }>;
  onActivate?: () => void;
  onOpenSession: (sessionId: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const { loading, shown, scan, ending, stale } = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [path, setPath] = useState<readonly string[]>([]);
  const [sort, setSort] = useState<Sort>({ column: "size", descending: true });
  const [ticked, setTicked] = useState<ReadonlySet<string>>(new Set());
  const [cleaning, setCleaning] = useState<readonly UsageNode[] | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const alive = useRef(false);
  useEffect(() => {
    alive.current = true;
    const detach = hub.attach(epoch);
    return () => { alive.current = false; detach(); };
  }, [hub, epoch]);
  // A measurement replaces the rows: what was ticked is let go.
  const measuring = !!scan;
  useEffect(() => { if (measuring) setTicked(new Set()); }, [measuring]);

  const view = scan?.view ?? shown?.view ?? null;
  const current = !scan && !!shown?.current;
  const root = useMemo(() => view ? usageTree(view, spaces) : null, [view, spaces]);
  const trail = useMemo(() => root ? nodePath(root, path) : [], [root, path]);
  const level = trail[trail.length - 1] ?? null;
  const label = useCallback((node: UsageNode): string => {
    switch (node.kind) {
      case "root": return t("Everything");
      case "space": return node.name || t("Not in a space");
      case "worktrees": return t("Worktrees");
      // The sessions of the project are one row: their number goes with its name.
      case "sessions": return `${t("Sessions")} (${node.project?.sessions.count ?? 0})`;
      default: return node.name;
    }
  }, [t]);
  // The checkouts of the view by their path: the names of the ones a folder holds.
  const byPath = useMemo(() => view ? checkoutsByPath(view) : new Map<string, DiskUsageCheckout>(), [view]);
  const held = useCallback((path: string) => byPath.get(path), [byPath]);
  const rows = useMemo(() => level ? sortNodes(level.children, sort, label, locale) : [], [level, sort, label, locale]);
  const donut = useMemo(() => levelDonut(level?.children ?? []), [level]);

  function go(next: readonly string[]) { setPath(next); setTicked(new Set()); setNotice(null); }
  function open(node: UsageNode) {
    if (node.kind === "session" && node.session) onOpenSession(node.session.id);
    else if (drillable(node)) go([...trail.slice(1).map(value => value.id), node.id]);
  }
  // A segment drills in as the name of its row does; one that has no level, or "Other", does nothing.
  const select = (id: string) => {
    const node = level?.children.find(child => child.id === id);
    if (node && drillable(node)) open(node);
  };

  const external = useExternalOpener();
  const fileManager = t(openInFileManagerKey(external.platform));
  function reveal(node: UsageNode) {
    const opener = external.opener, project = node.project;
    if (!opener || !project) return;
    if (node.kind === "project" || node.checkout?.project) void opener.openFolder("explorer", project.id, null);
    else if (node.checkout) void opener.openWorktree("explorer", project.id, node.checkout.path);
  }

  // The project whose worktree is being removed: it is marked as changed once one went.
  const pendingRemoval = useRef<string | null>(null);
  const removal = useWorktreeRemoval({ epoch: epoch ?? "", api: worktreesApi, onRemoved: outcomes => {
    if (pendingRemoval.current) hub.removed(pendingRemoval.current, outcomes.filter(outcome => outcome.status === "ok").map(outcome => outcome.path));
  } });
  async function askRemoval(node: UsageNode) {
    const project = node.project, checkout = node.checkout;
    if (!epoch || !project || !checkout) return;
    setNotice(null);
    const reply = await worktreesApi.inventory({ expectedEpoch: epoch, projectId: project.id }, { timeoutMilliseconds: 30_000 })
      .then(value => inventoryReply(value, project.id), () => "read_failed");
    if (!alive.current) return;
    if (typeof reply === "string") { setNotice(t(inventoryFailure(reply))); return; }
    const row = reply.rows.find(value => sameFolder(value.path, checkout.path));
    if (!row) { setNotice(t("Git no longer lists this worktree. Measure again.")); return; }
    if (!removable(row)) {
      setNotice(t(row.protection === "in_use" || row.busy ? "A session is working here" : row.protection === "holds_worktree" ? "Another worktree is inside its folder: remove that one first."
        : row.protection === "locked" || row.locked
        ? "Git keeps this worktree locked: unlock it with git to remove it." : "The checkout the repository lives in is not removed."));
      return;
    }
    pendingRemoval.current = project.id;
    removal.begin({ id: project.id, name: project.name }, [row]);
  }

  function cleanable(node: UsageNode) { return node.checkout && !stale.paths.has(node.checkout.path) ? cleanBlock(node.checkout, current) === null : false; }
  const checkoutRows = rows.filter(node => node.kind === "checkout");
  const chosen = checkoutRows.filter(node => ticked.has(node.id));
  const every = checkoutRows.filter(cleanable);
  const all = every.length > 0 && every.every(node => ticked.has(node.id));
  const toggle = (node: UsageNode) => setTicked(value => { const next = new Set(value); if (!next.delete(node.id)) next.add(node.id); return next; });

  const headings: readonly (readonly [SortColumn, "Name" | "Size" | "Build output" | "Last used" | "Git state", string])[] = [
    ["name", "Name", "disk-usage-cell-name"], ["size", "Size", "disk-usage-cell-size"], ["buildOutput", "Build output", "disk-usage-cell-build"],
    ["lastUsed", "Last used", "disk-usage-cell-used"], ["verdict", "Git state", "disk-usage-cell-git"]];
  const sortBy = (column: SortColumn) => setSort(value => value.column === column ? { column, descending: !value.descending } : { column, descending: column !== "name" });

  function marks(node: UsageNode) {
    const checkout = node.checkout;
    const list: { key: string; text: string; title?: string; tone: string }[] = [];
    if (checkout) {
      if (checkout.project) list.push({ key: "project", text: t("Project folder"), tone: "main", title: t("This is the folder of the project: it is not a worktree to remove.") });
      else if (checkout.main) list.push({ key: "main", text: t("Main checkout"), tone: "main", title: t("The checkout the repository lives in is not removed.") });
      if (checkout.inUse) list.push({ key: "busy", text: t("In use"), tone: "busy", title: t("A session is working here") });
      if (checkout.locked) list.push({ key: "locked", text: t("Locked"), tone: "locked", title: t("Git keeps this worktree locked: unlock it with git to remove it.") });
      if (checkout.protection === "holds_worktree") list.push({ key: "holds", text: t("Holds a worktree"), tone: "locked", title: t("Another worktree is inside its folder: remove that one first.") });
      if (checkout.heldCheckouts.length > 0) list.push({ key: "held", text: t("Holds checkouts"), tone: "main",
        title: t("Inside its folder: {names}", { names: checkout.heldCheckouts.map(path => byPath.get(path)?.name ?? path).join(", ") }) });
      if (checkout.missing) list.push({ key: "missing", text: t("Folder gone"), tone: "missing", title: t("Git still lists this worktree, and its folder is gone.") });
      if (checkout.runningApplication) list.push({ key: "app", text: t("Runs this application"), tone: "busy", title: t("This application runs from this folder: its build output is not cleaned.") });
      if (stale.paths.has(checkout.path)) list.push({ key: "removed", text: t("Removed"), tone: "missing", title: t("Removed since the measurement.") });
    }
    if (node.kind === "project" && node.project?.archived) list.push({ key: "archived", text: t("Archived"), tone: "locked" });
    if (node.kind === "project" && node.project && stale.projects.has(node.project.id)) list.push({ key: "stale", text: t("Changed"), tone: "locked", title: t("Changed since the measurement: measure again.") });
    return list;
  }
  function details(node: UsageNode): string[] {
    const lines: string[] = [];
    if (node.measuredWith) lines.push(t("Measured with {project}: they share one repository.", { project: node.measuredWith }));
    if (node.sharedWith?.length) lines.push(t("Also the repository of {projects}.", { projects: node.sharedWith.join(", ") }));
    if (node.sameFolderAs) lines.push(t("Git lists this folder twice: it is measured as {name}.", { name: node.sameFolderAs }));
    if (node.kind === "project" && node.project?.status === "missing") lines.push(t("The folder of this project is gone."));
    if (node.kind === "project" && node.project?.checkoutsTruncated) lines.push(t("Git lists more worktrees than were measured."));
    if (node.problem && node.kind !== "checkout") lines.push(t("It could not be measured: {problem}", { problem: node.problem }));
    if (node.kind === "sessions" && node.moreSessions) lines.push(t("{count} smaller sessions are not listed.", { count: node.moreSessions }));
    if (node.kind === "session" && node.session) {
      const where = node.session.checkout ? node.project?.checkouts.find(value => value.path === node.session!.checkout) : null;
      if (where) lines.push(t("Works in {name}", { name: where.project ? t("the project folder") : where.name }));
      if (node.session.running) lines.push(t("Running"));
    }
    return lines;
  }
  function verdictCell(node: UsageNode) {
    if (!node.checkout) return null;
    if (!node.verdict) return <span className="disk-usage-muted">—</span>;
    const text = verdictLabel(node.verdict, t);
    const reasons = verdictReasons(node.checkout, t, locale, held);
    // Nothing local with nothing to say is a word, not a button; any other verdict always opens on its reasons.
    if (reasons.length === 0) return <span className="disk-usage-verdict" data-verdict={node.verdict}>{text}</span>;
    // The reasons open beside the verdict, over the table: the row keeps its height.
    return <PopoverNext placement="bottom-start" popoverClassName="disk-usage-reasons"
      content={<ul>{reasons.map((reason, index) => <li key={index}>{reason}</li>)}</ul>}>
      <button type="button" className="disk-usage-verdict settings-own-button" data-verdict={node.verdict} aria-haspopup="dialog">
        {text}</button>
    </PopoverNext>;
  }
  function actions(node: UsageNode) {
    const project = node.project;
    const checkout: DiskUsageCheckout | undefined = node.checkout;
    const name = label(node);
    if (node.kind === "session") return <Button variant="minimal" size="small" icon={<AppIcon name="open" size={14} />} aria-label={t("Open the session {name}", { name })}
      title={t("Open session")} onClick={() => open(node)} />;
    const revealButton = external.opener && external.platform && (node.kind === "project" || checkout && !checkout.missing) && project && project.status !== "missing"
      && <Button variant="minimal" size="small" icon={<AppIcon name="folder" size={14} />} aria-label={t(openInFileManagerKey(external.platform, true), { name })}
        title={fileManager} onClick={() => reveal(node)} />;
    if (node.kind === "project") return revealButton;
    if (!checkout) return null;
    const gone = stale.paths.has(checkout.path);
    const block = gone ? null : cleanBlock(checkout, current);
    // Never the folder of a project, nor the checkout the repository lives in.
    const canRemove = !checkout.project && !checkout.main && checkout.projectIds.length === 0;
    // Another worktree inside its folder would go with it: it says so, and is not removed.
    const holds = checkout.protection === "holds_worktree";
    return <>
      <Button variant="minimal" size="small" icon={<AppIcon name="eraser" size={14} />} disabled={gone || !!block || !!scan}
        aria-label={block ? `${t("Clean the build output of {name}", { name })}: ${cleanBlockText(block, t)}` : t("Clean the build output of {name}", { name })}
        title={block ? cleanBlockText(block, t) : t("Clean build output…")} onClick={() => setCleaning([node])} />
      {canRemove && <Button variant="minimal" size="small" icon={<AppIcon name="trash" size={14} />} disabled={gone || holds || !epoch || !!scan || removal.working}
        aria-label={holds ? `${t("Remove the worktree {name}", { name })}: ${t("Another worktree is inside its folder: remove that one first.")}` : t("Remove the worktree {name}", { name })}
        title={holds ? t("Another worktree is inside its folder: remove that one first.") : t("Remove worktree…")} onClick={() => void askRemoval(node)} />}
      {revealButton}
    </>;
  }

  const done = view?.done ?? null;
  const progress = scan?.progress;
  const age = shown?.view.done ? sessionTime(shown.view.done.completedAt, locale) : null;
  const columns = 7;
  const titleId = useId();
  const measureButton = scan ? <Button icon={<AppIcon name="stop" size={14} />} text={t("Stop")} onClick={() => hub.stop()} />
    : <Button intent="primary" icon={<AppIcon name="refresh" size={14} />} text={t("Measure")} disabled={!epoch || loading || removal.working}
      onClick={() => { setNotice(null); hub.measure(); }} />;
  const total = root && root.size.state !== "unknown" && <span className="disk-usage-total" title={done ? t("Everything measured, each byte once.") : undefined}>{sizeText(root.size, locale, t)}</span>;
  const ageText = !scan && age && <span className="disk-usage-age" title={age.title}>{t("Measured {time}", { time: age.label })}</span>;
  return <div className="disk-usage-panel" data-embedded={embedded ? "true" : undefined} aria-label={t("Disk usage")} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    {embedded ? <header className="disk-usage-header">
      {total}{ageText}
      <div className="disk-usage-tools">{measureButton}
        <Button variant="minimal" icon={<AppIcon name="openExternal" size={14} />} text={t("Open as tab")} onClick={embedded.onOpenTab} /></div>
    </header>
    : <header className="disk-usage-header">
      <div className="disk-usage-heading"><AppIcon name="diskUsage" size={18} /><h1>{t("Disk usage")}</h1>{total}</div>
      <div className="disk-usage-tools">{ageText}{measureButton}</div>
    </header>}
    <div className="disk-usage-body">
      {scan && <div className="disk-usage-progress" role="status">
        <ActivitySpinner size={14} />
        <span>{!progress || progress.phase === "sessions" ? t("Measuring the sessions…")
          : t("Measuring the projects: {done} of {total} checkouts", { done: progress.checkoutsDone, total: progress.checkoutsTotal })}</span>
        {progress && storageBytes(progress.files) > 0 && <small>{t("{count} files counted", { count: new Intl.NumberFormat(locale).format(storageBytes(progress.files)) })}</small>}
        {progress?.current && <code title={progress.current}>{progress.current}</code>}
      </div>}
      {!scan && shown && <p className="disk-usage-hint">{t("Git state is as of the last fetch: measuring never contacts a remote.")}</p>}
      {ending?.kind === "stopped" && <Callout compact intent="primary" role="status">{t("The measurement was stopped: the last one is shown.")}</Callout>}
      {ending?.kind === "failed" && <Callout compact intent="warning" role="alert">{ending.problem ? t("The measurement failed: {problem}", { problem: ending.problem }) : t("The measurement could not be read.")}</Callout>}
      {ending?.kind === "refused" && <Callout compact intent="warning" role="alert">{t(refusals[ending.status] ?? "The measurement could not be read.")}</Callout>}
      {notice && <Callout compact intent="warning" role="alert" className="disk-usage-notice">{notice}</Callout>}
      {done?.totalPartial && <Callout compact intent="warning">{t("Some things could not be read: the sizes are a lower bound.")}</Callout>}
      {done && !done.sessionsKnown && <Callout compact intent="warning">{t("The sizes of the sessions could not be read.")}</Callout>}
      {done && done.unreadable.length > 0 && <Callout compact intent="warning" className="disk-usage-unreadable">
        {t(done.unreadable.length === 1 ? "{count} project could not be read:" : "{count} projects could not be read:", { count: done.unreadable.length })}
        <ul>{done.unreadable.map(file => <li key={file}><code>{file}</code></li>)}</ul></Callout>}
      {shown && !current && !scan && <p className="disk-usage-hint">{t("Measure again to clean build output: only a measurement of this run can be cleaned.")}</p>}
      {!root ? <NonIdealState className="disk-usage-empty" icon={loading ? <ActivitySpinner size={28} /> : <AppIcon name="diskUsage" size={36} />}
          title={loading ? t("Loading…") : t("Nothing measured yet")}
          description={loading ? undefined : t("Measure what the projects, their worktrees and their sessions hold.")} />
        : <>
          <nav className="disk-usage-trail" aria-label={t("Where you are")}>
            {trail.map((node, index) => <Fragment key={node.id}>
              {index > 0 && <AppIcon name="chevronRight" size={12} />}
              {index === trail.length - 1 ? <span aria-current="page">{label(node)}</span>
                : <button type="button" className="settings-own-button" onClick={() => go(trail.slice(1, index + 1).map(value => value.id))}>{label(node)}</button>}
            </Fragment>)}
          </nav>
          {/* The table is the legend of the donut: the swatch of a row is the color of its segment. */}
          <div className="storage-distribution disk-usage-distribution" data-chart={donut.slices.length > 0 || undefined}>
          <div className="disk-usage-table-scroll">
            <table className="disk-usage-table" aria-labelledby={titleId}>
              <caption id={titleId} className="sr-only">{t("What {name} holds", { name: label(level!) })}</caption>
              <thead><tr>
                <th className="disk-usage-cell-select">{checkoutRows.length > 0 && <Checkbox checked={all} indeterminate={!all && chosen.length > 0} disabled={!every.length || !!scan}
                  aria-label={t("Select every checkout whose build output can be cleaned")} onChange={() => setTicked(all ? new Set() : new Set(every.map(node => node.id)))} />}</th>
                {headings.map(([column, text, className]) => <Fragment key={column}><th className={className} aria-sort={sort.column === column ? sort.descending ? "descending" : "ascending" : undefined}>
                  <button type="button" className="settings-own-button" onClick={() => sortBy(column)}>{t(text)}{sort.column === column && <AppIcon name={sort.descending ? "arrowDown" : "arrowUp"} size={11} />}</button></th></Fragment>)}
                <th className="disk-usage-cell-actions"><span className="sr-only">{t("Actions")}</span></th>
              </tr></thead>
              <tbody>
                {rows.length === 0 && <tr><td colSpan={columns} className="disk-usage-none">{scan ? t("Measuring…") : t("Nothing here.")}</td></tr>}
                {rows.map(node => {
                  const block = node.checkout ? cleanBlock(node.checkout, current) : null;
                  const can = cleanable(node);
                  const lines = details(node);
                  const used = node.lastUsed ? sessionTime(node.lastUsed, locale) : null;
                  const opens = node.kind === "session" || drillable(node);
                  return <Fragment key={node.id}>
                    <tr className="disk-usage-row" data-node={node.id} data-kind={node.kind} data-selected={ticked.has(node.id) || undefined}>
                      {/* A checkbox that cannot be ticked says why on its cell: a disabled input shows no tooltip of its own. */}
                      <td className="disk-usage-cell-select" title={node.kind === "checkout" && !can ? block ? cleanBlockText(block, t) : t("Removed") : undefined}>
                        {node.kind === "checkout" && <Checkbox checked={ticked.has(node.id)} disabled={!can || !!scan}
                        aria-label={can ? t("Select {name}", { name: label(node) }) : `${label(node)}: ${block ? cleanBlockText(block, t) : t("Removed")}`} onChange={() => toggle(node)} />}</td>
                      <td className="disk-usage-cell-name">
                        <span className="disk-usage-name">
                          {donut.colors.has(node.id) ? <ShareSwatch index={donut.colors.get(node.id)!} className="disk-usage-swatch" /> : <span className="disk-usage-swatch" aria-hidden="true" />}
                          <AppIcon name={node.checkout?.project ? "folder" : icons[node.kind]} size={15} />
                          {opens ? <button type="button" className="disk-usage-open settings-own-button" onClick={() => open(node)}
                            title={node.kind === "session" ? t("Open session") : node.checkout?.path ?? t("Open {name}", { name: label(node) })}>{label(node)}</button>
                            : <strong title={node.checkout?.path}>{label(node)}</strong>}
                          {marks(node).map(mark => <span key={mark.key} className="disk-usage-mark" data-tone={mark.tone} title={mark.title}>{mark.text}</span>)}
                        </span>
                        {lines.map((line, index) => <small key={index} className="disk-usage-detail" title={line}>{line}</small>)}
                        {node.checkout?.problem && <small className="disk-usage-detail" data-problem="true">{t("It could not be measured: {problem}", { problem: node.checkout.problem })}</small>}
                      </td>
                      <td className="disk-usage-cell-size" data-state={node.size.state}>{sizeText(node.size, locale, t)}</td>
                      <td className="disk-usage-cell-build" data-state={node.buildOutput.state}>{sizeText(node.buildOutput, locale, t)}</td>
                      <td className="disk-usage-cell-used">{used ? <time dateTime={used.dateTime} title={used.title}>{used.label}</time> : <span className="disk-usage-muted">—</span>}</td>
                      <td className="disk-usage-cell-git">{verdictCell(node)}</td>
                      <td className="disk-usage-cell-actions">{actions(node)}</td>
                    </tr>
                  </Fragment>;
                })}
              </tbody>
            </table>
          </div>
          {donut.slices.length > 0 && <ShareDonut ariaLabel={t("What {name} holds", { name: label(level!) })} visible={visible} onSelect={select}
            segments={donut.slices.map(slice => ({ name: slice.id === null ? t("Other") : label(slice.nodes[0]), value: slice.value, id: slice.id }))} />}
          </div>
          {/* Under the table: what is above it is the same at every level, so the table does not move when the level changes. */}
          {level && level.kind === "root" && trail.length === 1 && spaces.length > 1 && <p className="disk-usage-hint">{t("A project in several spaces is in each: the total counts it once.")}</p>}
        </>}
    </div>
    {chosen.length > 0 && <footer className="disk-usage-footer">
      <span role="status">{t("{count} selected", { count: chosen.length })}</span>
      <span>
        <Button variant="minimal" text={t("Clear selection")} onClick={() => setTicked(new Set())} />
        <Button intent="danger" icon={<AppIcon name="eraser" size={14} />} disabled={!!scan} text={t("Clean the build output of {count}…", { count: chosen.length })}
          onClick={() => setCleaning(chosen)} />
      </span>
    </footer>}
    {cleaning && epoch && <DiskUsageClean hub={hub} nodes={cleaning} current={current} label={label}
      onClose={() => { setCleaning(null); setTicked(new Set()); }} />}
    {removal.phase && <AppWindow storageKey="codealta.desktop.window.diskUsageRemoval.v1" className="worktree-manager-dialog" titleId={`${titleId}-removal`} title={t("Remove worktree")}
      preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(560, viewport.height - 40) })} minimumSize={{ width: 360, height: 300 }}
      onClose={() => { if (!removal.working) removal.back(); }} closeLabel={t("Close")} keepOnOutsidePress
      onCancel={event => { event.preventDefault(); removal.back(); }}
      onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); removal.back(); } }}>
      <div className="worktree-manager" data-phase={removal.phase.kind}>{removal.view}</div>
    </AppWindow>}
  </div>;
}
