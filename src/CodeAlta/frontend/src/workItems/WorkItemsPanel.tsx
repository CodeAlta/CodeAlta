import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { Alert, Button, Callout, HTMLSelect, InputGroup, SegmentedControl, Tab, Tabs, Tag } from "@blueprintjs/core";
import { useRunsWith, type RunModelsLoader, type RunProvider, type RunsWith } from "./runsWith";
import type { WorkspaceProject, WorkspaceSession } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { ProviderIcon } from "../ProviderIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { WorkItemTags, WorkItemText, WorkStartButtons } from "./WorkItemCards";
import { filterWorkItems, stageCounts, taskCategoryIcon, taskCategoryTone, workFilterLabel, workFilters, workItemKey, workItems, workKindIcon, workStatusLabel,
  workStatusTone, type WorkFilter, type WorkItem, type WorkKind, type WorkStart } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";

/** What another part of the window asks the tab to show: the items of a project, or one item. */
export type WorkItemsFocus = Readonly<{ projectId: string | null; key?: string | null }>;

/**
 * The Work items tab: the follow-up tasks agents proposed and the plans, of one project or of all. What waits
 * for a decision, what is being done, what is set aside and what is closed are separate lists, so that what
 * is done does not hide what is left. An item is read on the right, and decided there: started, kept for
 * later, marked done, or removed.
 */
export function WorkItemsPanel({ hub, projects, sessions, runningSessions, projectId, visible, focus, onFocused, onActivate, onStart, onOpenSession, onOpenFile, onOpenSettings, providers = noProviders, loadModels = null }: {
  hub: WorkItemsHub;
  projects: readonly WorkspaceProject[];
  sessions: readonly WorkspaceSession[];
  /** The sessions that are working now. */
  runningSessions: ReadonlySet<string>;
  /** The project selected in the window, which the tab shows first. */
  projectId: string | null;
  visible: boolean;
  focus?: WorkItemsFocus | null;
  onFocused?: () => void;
  onActivate: () => void;
  /** Starts the work of an item in a new session; resolves when the session exists or the start was refused. */
  onStart: (item: WorkItem, start: WorkStart, runsWith: RunsWith | null) => Promise<boolean>;
  /** The enabled providers a new session can run with; none when the window cannot start one. */
  providers?: readonly RunProvider[];
  /** Lists the models of a provider. */
  loadModels?: RunModelsLoader | null;
  onOpenSession: (sessionId: string, projectId: string) => void;
  /** Opens the file of an item in the code editor of its project. */
  onOpenFile: (projectId: string, file: string) => void;
  onOpenSettings: () => void;
}) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [scope, setScope] = useState<string | null>(projectId);
  const [kind, setKind] = useState<WorkKind | "all">("all");
  const [stage, setStage] = useState<WorkFilter>("todo");
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [removing, setRemoving] = useState<WorkItem | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  const sessionIds = useMemo(() => new Set(sessions.map(session => session.id)), [sessions]);
  const sessionById = useMemo(() => new Map(sessions.map(session => [session.id, session])), [sessions]);
  const projectById = useMemo(() => new Map(projects.map(project => [project.id, project])), [projects]);
  const all = useMemo(() => workItems(state.projects, sessionIds).filter(item => projectById.has(item.projectId)), [state.projects, sessionIds, projectById]);
  // A project that has nothing is not one to choose: the list would be empty.
  const scoped = scope !== null && all.some(item => item.projectId === scope) ? scope : null;
  const counts = stageCounts(all, scoped, kind);
  const shown = useMemo(() => filterWorkItems(all, { projectId: scoped, kind, stage, query }), [all, scoped, kind, stage, query]);
  const current = shown.find(item => workItemKey(item) === selected) ?? shown[0] ?? null;
  useEffect(() => { if (visible) void hub.refresh(); }, [hub, visible]);
  useEffect(() => {
    if (!focus || !state.loaded) return;
    setScope(focus.projectId);
    const item = focus.key ? all.find(candidate => workItemKey(candidate) === focus.key) : null;
    if (item) { setStage(item.stage); setKind("all"); setQuery(""); setSelected(focus.key!); }
    onFocused?.();
  }, [focus, state.loaded]);

  const during = async (item: WorkItem, work: () => Promise<boolean>) => {
    const key = workItemKey(item);
    setBusy(value => new Set(value).add(key));
    setNotice(null);
    try { if (!await work()) setNotice("The change was not made."); }
    finally { setBusy(value => { const next = new Set(value); next.delete(key); return next; }); }
  };
  const act = (item: WorkItem, action: string, value?: string) => void during(item, async () => (await hub.act(item, action, { value })).ok);
  // A start says itself why it was refused.
  const start = (item: WorkItem, way: WorkStart, runsWith: RunsWith | null) => void during(item, async () => { await onStart(item, way, runsWith); return true; });

  if (!state.loaded) return <div className="work-page" onPointerDown={onActivate}><p className="work-empty"><ActivitySpinner size={16} /></p></div>;
  if (!state.available) return <div className="work-page" onPointerDown={onActivate}><p className="work-empty">{t("Work items are unavailable in this window.")}</p></div>;
  const withItems = projects.filter(project => all.some(item => item.projectId === project.id));
  const groups = scoped === null ? withItems.filter(project => shown.some(item => item.projectId === project.id)) : withItems.filter(project => project.id === scoped);
  return <div className="work-page" onPointerDown={onActivate} data-empty={all.length === 0 || undefined}>
    <header className="work-header">
      <span className="work-emblem"><AppIcon name="task" size={22} /></span>
      <div><h2>{t("Work items")}{!state.complete && <ActivitySpinner size={13} />}</h2><p>{t("The follow-up tasks agents proposed and the plans of your projects.")}</p></div>
      <Button variant="minimal" icon={<AppIcon name="settings" size={16} />} aria-label={t("Work item settings")} title={t("Work item settings")} onClick={onOpenSettings} />
      <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} aria-label={t("Reload")} title={t("Read the files again")} onClick={() => void hub.refresh()} />
    </header>
    {notice && <Callout intent="danger" compact role="alert" className="work-notice">{t(notice)}</Callout>}
    {all.length === 0 && !state.complete ? <p className="work-empty"><ActivitySpinner size={16} /></p>
      : all.length === 0
      ? <section className="work-hero">
          <h3>{t("Nothing is waiting")}</h3>
          <p>{t("When an agent finds a gap, a problem or an improvement beside what it was asked, it proposes a task here. Plans written in Plan mode are listed here too, until they are done.")}</p>
        </section>
      : <>
        <div className="work-toolbar">
          <SegmentedControl size="small" value={kind} onValueChange={value => setKind(value as WorkKind | "all")}
            options={[{ value: "all", label: t("All") }, { value: "task", label: t("Tasks") }, { value: "plan", label: t("Plans") }]} />
          <HTMLSelect aria-label={t("Project")} value={scoped ?? ""} onChange={event => setScope(event.target.value || null)}>
            <option value="">{t("All projects")}</option>
            {withItems.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}
          </HTMLSelect>
          <InputGroup size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query} placeholder={t("Search work items")}
            aria-label={t("Search work items")} onChange={event => setQuery(event.target.value)} />
        </div>
        <Tabs id="work-stages" className="work-stages" selectedTabId={stage} onChange={next => setStage(next as WorkFilter)}>
          {workFilters.map(filter => <Tab key={filter} id={filter} title={<>{t(workFilterLabel(filter))}<span className="work-count" data-zero={counts[filter] === 0 || undefined}>{counts[filter]}</span></>} />)}
        </Tabs>
        <div className="work-body">
          <div className="work-list" role="listbox" aria-label={t(workFilterLabel(stage))}>
            {shown.length === 0 && <p className="work-empty">{t(query.trim() ? "No work item matches." : stage === "todo" ? "Nothing waits for a decision." : stage === "running" ? "Nothing is in progress."
              : stage === "later" ? "Nothing is set aside for later." : "Nothing here. The settings say whether closed tasks and completed plans are kept.")}</p>}
            {groups.map(project => <section key={project.id} className="work-group">
              {scoped === null && <h3><AppIcon name="folder" size={13} />{project.name}<span className="work-count">{shown.filter(item => item.projectId === project.id).length}</span></h3>}
              {shown.filter(item => item.projectId === project.id).map(item => {
                const key = workItemKey(item);
                const runner = item.runner ? sessionById.get(item.runner) : undefined;
                return <button type="button" key={key} role="option" aria-selected={current !== null && workItemKey(current) === key} className="work-row" data-kind={item.kind}
                  data-selected={current !== null && workItemKey(current) === key || undefined} onClick={() => setSelected(key)}>
                  <span className="work-row-glyph" data-tone={item.kind === "task" ? taskCategoryTone(item.category) : "primary"}>
                    <AppIcon name={item.kind === "task" ? taskCategoryIcon(item.category) : workKindIcon(item.kind)} size={16} /></span>
                  <span className="work-row-text"><strong>{item.title}</strong>
                    <small>{item.summary ?? item.statusText ?? item.file}</small></span>
                  <span className="work-row-meta">
                    {runner && <span className="work-runner" title={runner.title}>{runningSessions.has(runner.id) ? <ActivitySpinner size={11} /> : <AppIcon name="chat" size={11} />}<span>{runner.title}</span></span>}
                    <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>
                  </span>
                </button>;
              })}
            </section>)}
          </div>
          <aside className="work-detail">
            {current ? <WorkItemDetail key={workItemKey(current)} hub={hub} item={current} busy={busy.has(workItemKey(current))} projectName={projectById.get(current.projectId)?.name ?? null}
                providers={providers} loadModels={loadModels}
                runner={current.runner ? sessionById.get(current.runner) ?? null : null} runnerWorking={!!current.runner && runningSessions.has(current.runner)}
                preferredStart={state.settings.start} onStart={(way, runsWith) => start(current, way, runsWith)} onAct={(action, value) => act(current, action, value)}
                onRemove={() => setRemoving(current)} onOpenSession={() => current.runner && onOpenSession(current.runner, current.projectId)}
                onOpenFile={() => onOpenFile(current.projectId, current.file)} />
              : <p className="work-empty">{t("Choose a work item to read it.")}</p>}
          </aside>
        </div>
      </>}
    <Alert isOpen={!!removing} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t("Remove")} canEscapeKeyCancel canOutsideClickCancel
      onCancel={() => setRemoving(null)} onConfirm={() => { const item = removing; setRemoving(null); if (item) act(item, "remove"); }}>
      <p>{removing && t(removing.kind === "plan" ? "Remove the plan \"{title}\"? Its file is deleted." : "Remove the task \"{title}\"? Its file is deleted.", { title: removing.title })}</p>
    </Alert>
  </div>;
}

const noProviders: readonly RunProvider[] = [];

function WorkItemDetail({ hub, item, busy, projectName, runner, runnerWorking, preferredStart, providers, loadModels, onStart, onAct, onRemove, onOpenSession, onOpenFile }: {
  hub: WorkItemsHub; item: WorkItem; busy: boolean; projectName: string | null;
  runner: WorkspaceSession | null; runnerWorking: boolean; preferredStart: string;
  providers: readonly RunProvider[]; loadModels: RunModelsLoader | null;
  onStart: (start: WorkStart, runsWith: RunsWith | null) => void; onAct: (action: string, value?: string) => void; onRemove: () => void; onOpenSession: () => void; onOpenFile: () => void;
}) {
  const { t } = useShellLanguage();
  const task = item.kind === "task";
  const startable = item.stage === "todo" || item.stage === "later";
  // What the new session runs with: what the item was proposed with, else the defaults, until the user changes it.
  const run = useRunsWith(startable ? providers : noProviders, item.runsWith, startable ? loadModels : null);
  return <section className="work-detail-card" aria-label={item.title}>
    <header>
      <div className="work-detail-kind"><AppIcon name={workKindIcon(item.kind)} size={14} />{t(task ? "Task" : "Plan")}
        <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>{busy && <ActivitySpinner size={13} />}</div>
      <h3>{item.title}</h3>
      <WorkItemTags item={item} projectName={projectName} />
      {item.statusText && <p className="work-detail-status">{item.statusText}</p>}
      {runner && <button type="button" className="work-runner work-runner-link" title={t("Open the session that does it")} onClick={onOpenSession}>
        {runnerWorking ? <ActivitySpinner size={12} /> : <AppIcon name="chat" size={12} />}<span>{runner.title}</span><AppIcon name="chevronRight" size={12} /></button>}
    </header>
    <div className="work-detail-actions">
      {startable && run.provider && <div className="work-runs-with" role="group" aria-label={t("Runs with")}>
        <span className="work-runs-with-label"><ProviderIcon providerKey={run.provider.id} size={13} />{t("Runs with")}</span>
        <HTMLSelect aria-label={t("Provider")} value={run.provider.id} disabled={busy} onChange={event => run.chooseProvider(event.target.value)}>
          {providers.map(provider => <option key={provider.id} value={provider.id}>{provider.isDefault ? t("{name} (default)", { name: provider.name }) : provider.name}</option>)}
        </HTMLSelect>
        <HTMLSelect aria-label={t("Model")} value={run.modelId ?? ""} disabled={busy || run.loading || run.models.length === 0} onChange={event => run.chooseModel(event.target.value)}>
          {run.modelId === null && <option value="">{t(run.loading ? "Loading…" : "Host default")}</option>}
          {run.models.map(model => <option key={model.id} value={model.id}>{model.name || model.id}</option>)}
        </HTMLSelect>
        {run.efforts.length > 0 && <HTMLSelect aria-label={t("Reasoning")} value={run.reasoningEffort ?? ""} disabled={busy} onChange={event => run.chooseEffort(event.target.value)}>
          {run.efforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
        </HTMLSelect>}
      </div>}
      {startable && <WorkStartButtons preferred={preferredStart} here={false} disabled={busy} onStart={way => onStart(way, run.selection)} />}
      <div className="work-detail-buttons">
        {task && item.stage === "todo" && <Button size="small" disabled={busy} icon={<AppIcon name="later" size={14} />} onClick={() => onAct("later")}>{t("Later")}</Button>}
        {task && (item.stage === "later" || item.stage === "closed") && <Button size="small" disabled={busy} icon={<AppIcon name="restore" size={14} />} onClick={() => onAct("reopen")}>{t("Back to To do")}</Button>}
        {item.stage === "running" && item.runner && <Button size="small" disabled={busy} icon={<AppIcon name="restore" size={14} />} title={t("No session does it any more.")}
          onClick={() => onAct("release")}>{t("Back to To do")}</Button>}
        {task && item.stage !== "closed" && <Button size="small" disabled={busy} icon={<AppIcon name="check" size={14} />} onClick={() => onAct("complete")}>{t("Mark done")}</Button>}
        {!task && item.status === "draft" && <Button size="small" disabled={busy} icon={<AppIcon name="checked" size={14} />} onClick={() => onAct("status", "approved")}>{t("Mark ready")}</Button>}
        {!task && item.stage !== "closed" && <Button size="small" disabled={busy} icon={<AppIcon name="check" size={14} />} onClick={() => onAct("status", "done")}>{t("Mark done")}</Button>}
        {!task && item.stage === "closed" && <Button size="small" disabled={busy} icon={<AppIcon name="restore" size={14} />} onClick={() => onAct("status", "approved")}>{t("Reopen")}</Button>}
        {task && (item.stage === "todo" || item.stage === "later") && <Button size="small" disabled={busy} icon={<AppIcon name="close" size={14} />} onClick={() => onAct("dismiss")}>{t("Dismiss")}</Button>}
        <span className="work-detail-spacer" />
        <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} title={item.file} onClick={onOpenFile}>{t("Open file")}</Button>
        <Button size="small" variant="minimal" intent="danger" disabled={busy} icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove")} title={t("Remove")} onClick={onRemove} />
      </div>
    </div>
    <div className="work-detail-text"><WorkItemText hub={hub} item={item} /></div>
  </section>;
}
