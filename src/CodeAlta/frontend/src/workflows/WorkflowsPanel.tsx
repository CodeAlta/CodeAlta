import { Fragment, useContext, useEffect, useMemo, useState } from "react";
import { Button, HTMLSelect, InputGroup } from "@blueprintjs/core";
import type { WorkflowRunItem, WorkflowScriptItem, WorkflowStepItem, WorkspaceProject, WorkspaceSession } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { formatDuration } from "../toolCall";
import { ConfirmPopover } from "../ConfirmPopover";
import { MarkdownLinksContext } from "../MarkdownContent";
import { RunScriptWindow } from "./RunScriptWindow";
import { ScriptWindow } from "./ScriptWindow";
import { filterScripts, projectScriptPath, restoreSamplesShown, runDuration, runStateLabel, runTone, runsPage, runsRefreshMilliseconds, samplesShownKey, scopeLabel, shownRuns, sortRuns, stepColumns } from "./workflows";
import { createWorkflowsHub, type WorkflowsApi, type WorkflowsHub, type WorkflowsList } from "./workflowsHub";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import type { RunProvider } from "../workItems/runsWith";

const noProviders: readonly RunProvider[] = [];

/** The steps a script declares, small: a column for each stage, the steps of a stage side by side, an arrow between stages. */
function StepGraph({ steps }: { steps: readonly WorkflowStepItem[] }) {
  const { t } = useShellLanguage();
  const columns = useMemo(() => stepColumns(steps), [steps]);
  if (columns.length === 0) return null;
  return <ol className="workflow-graph" aria-label={t("Steps")}>
    {columns.map((column, index) => <Fragment key={column[0].id}>
      {index > 0 && <li className="workflow-graph-edge" aria-hidden="true"><AppIcon name="chevronRight" size={12} /></li>}
      <li className="workflow-graph-column"><ul>
        {column.map(step => <li key={step.id} className="workflow-graph-step" data-each={step.each || undefined} title={step.title === step.id ? step.id : `${step.title} · ${step.id}`}>
          <span>{step.title}</span>{step.each && <small aria-label={t("For each item")}>×</small>}
        </li>)}
      </ul></li>
    </Fragment>)}
  </ol>;
}

/**
 * A script of the library: its name, scope, description, steps and parameters, with Run…. Show the script opens its source
 * in a window; a script of the project or of yours opens in the code editor (Edit), and is deleted after a question (Delete).
 */
function ScriptCard({ script, onShow, onRun, onEdit, onDelete }: {
  script: WorkflowScriptItem; onShow: () => void; onRun: () => void;
  /** Opens the script in the code editor; null for a sample. */
  onEdit: (() => void) | null;
  /** Deletes the script and says why it could not, or null; null for a sample, which is never deleted. */
  onDelete: (() => Promise<string | null>) | null;
}) {
  const { t } = useShellLanguage();
  const [asking, setAsking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  async function remove() {
    if (!onDelete) return;
    setBusy(true);
    const failure = await onDelete();
    setBusy(false);
    if (failure === null) setAsking(false);
    else setProblem(failure || t("The script could not be deleted."));
  }
  return <li className="workflow-card" data-problem={script.problem ? true : undefined}>
    <div className="workflow-card-head">
      <span className="workflow-card-glyph"><AppIcon name="workflow" size={17} /></span>
      <span className="workflow-card-text">
        <strong title={script.path}>{script.title ?? script.name}</strong>
        <span>{script.title && <code>{script.name}</code>}<span className="workflow-scope" data-scope={script.scope}>{t(scopeLabel(script.scope))}</span></span>
      </span>
      <span className="workflow-card-actions">
        <Button size="small" variant="minimal" icon={<AppIcon name="code" size={14} />} aria-label={t("Show the script")} title={t("Show the script")} aria-haspopup="dialog" onClick={onShow} />
        {onEdit && <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} aria-label={t("Edit")} title={t("Edit")} onClick={onEdit} />}
        {onDelete && <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete")} title={t("Delete")} aria-haspopup="dialog"
          onClick={() => { setProblem(null); setAsking(true); }} />}
        <Button size="small" intent="primary" variant="minimal" disabled={!!script.problem} icon={<AppIcon name="play" size={14} />} onClick={onRun}>{t("Run…")}</Button>
        {asking && <ConfirmPopover always placement="bottom-end" title={t("Delete this script?")} subject={script.title ?? script.name} intent="danger" confirmLabel={t("Delete")}
          detail={t(script.scope === "project" ? "Its file is deleted from the .alta/scripts folder of the project." : "Its file is deleted from your ~/.alta/scripts folder.")}
          busy={busy} error={problem} onConfirm={() => void remove()} onCancel={() => setAsking(false)} />}
      </span>
    </div>
    <ScriptFacts script={script} />
  </li>;
}

/** What a script does, from its `meta`: its description (or why it does not parse), its steps drawn small and its parameters. */
function ScriptFacts({ script }: { script: WorkflowScriptItem }) {
  const { t } = useShellLanguage();
  return <>
    {script.problem ? <p className="workflow-card-problem" role="alert">{script.problem}</p>
      : script.description && <p className="workflow-card-description">{script.description}</p>}
    <StepGraph steps={script.steps} />
    {script.parameters.length > 0 && <p className="workflow-card-params">{script.parameters.map(parameter =>
      <span key={parameter.name} title={parameter.description ?? undefined}><code>{parameter.name}</code>{parameter.type && <small>{parameter.type}</small>}{parameter.required && <b aria-label={t("Required")}>*</b>}</span>)}</p>}
  </>;
}

// The parts of the library, in the order a project finds a name: its own scripts, yours, then the samples.
const libraryScopes = [
  { scope: "project", title: "This project", caption: "In the .alta/scripts folder of {project}, shared with it. They replace one of yours or a sample of the same name." },
  { scope: "global", title: "My scripts", caption: "In your ~/.alta/scripts folder: every project can run them, in the spaces that have them." },
  { scope: "builtin", title: "Samples", caption: "They come with CodeAlta, to run or to start from." },
] as const;

function RunMark({ state }: { state: string }) {
  const { t } = useShellLanguage();
  const tone = runTone(state);
  const label = t(runStateLabel(state));
  return <span className="automation-run-mark" data-tone={tone} role="img" aria-label={label} title={label}>
    {tone === "running" ? <ActivitySpinner size={12} /> : <AppIcon name={tone === "ok" ? "check" : tone === "failed" ? "error" : "minus"} size={13} />}</span>;
}

function RunList({ runs, sessions, now, onOpen }: {
  runs: readonly WorkflowRunItem[]; sessions: ReadonlyMap<string, WorkspaceSession>; now: number; onOpen: (run: WorkflowRunItem) => void;
}) {
  const { t, locale } = useShellLanguage();
  // The first runs, then more on demand: the side stays short whatever the number of runs.
  const [count, setCount] = useState(runsPage);
  const listed = shownRuns(runs, count);
  if (runs.length === 0) return <p className="automation-empty">{t("No run yet.")}</p>;
  return <><ul className="automation-runs">
    {listed.runs.map(run => {
      const session = sessions.get(run.sessionId);
      const when = sessionTime(run.startedAt, locale, now);
      const facts = [session?.title ?? null, t(runStateLabel(run.state)), formatDuration(runDuration(run, now)),
        run.calls === 1 ? t("1 call") : run.calls > 1 ? t("{count} calls", { count: run.calls }) : null].filter(Boolean).join(" · ");
      return <li key={run.id} data-tone={runTone(run.state)}>
        <button type="button" className="automation-run" title={t("Open run")} onClick={() => onOpen(run)}>
          <RunMark state={run.state} />
          <span className="automation-run-text"><strong>{run.title ?? run.id}</strong><span>{facts}</span></span>
          <time dateTime={when.dateTime} title={when.title}>{when.label}</time>
          <AppIcon name="chevronRight" size={14} />
        </button>
      </li>;
    })}
  </ul>
    {listed.more > 0 && <Button size="small" variant="minimal" className="workflow-runs-more" onClick={() => setCount(listed.runs.length + runsPage)}>
      {t("Show more…")}<span className="count">{listed.more}</span></Button>}
  </>;
}

// The runs, read again while the tab shows: often while one runs, else now and then.
function useRuns(hub: WorkflowsHub | null, visible: boolean, stamp: number) {
  const [runs, setRuns] = useState<WorkflowsList<WorkflowRunItem> | null>(null);
  useEffect(() => {
    if (!hub || !visible) return;
    const abort = new AbortController();
    let timer: ReturnType<typeof setTimeout> | null = null;
    async function read() {
      const value = await hub!.runs(abort.signal);
      if (abort.signal.aborted) return;
      if (value) setRuns(value);
      timer = setTimeout(() => void read(), runsRefreshMilliseconds(value?.items ?? []));
    }
    void read();
    return () => { abort.abort(); if (timer !== null) clearTimeout(timer); };
  }, [hub, visible, stamp]);
  return runs;
}

function useNow(visible: boolean, running: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!visible) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), running ? 1000 : 30_000);
    return () => clearInterval(timer);
  }, [visible, running]);
  return now;
}

/**
 * The Workflows tab: the scripts of the library a project sees, with their steps drawn small and Run…, and the runs of
 * scripts of CodeAlta, running first, each opening its session.
 */
export function WorkflowsPanel({ api, epoch, projects, sessions, projectId, visible, onActivate, onOpenSession, onOpenRun, onEditScript, providers = noProviders, sessionSettings = null }: {
  api: WorkflowsApi;
  /** The epoch of the host of the window; null in a window without a host of its own, undefined while it is not known. */
  epoch: string | null | undefined;
  /** The projects a script can run in. */
  projects: readonly WorkspaceProject[];
  sessions: readonly WorkspaceSession[];
  /** The project selected in the window, whose library the tab shows first. */
  projectId: string | null;
  visible: boolean;
  onActivate: () => void;
  onOpenSession: (sessionId: string) => void;
  /** Opens the tab of a run; without it, a run opens its session. */
  onOpenRun?: (run: Readonly<{ runId: string; title: string | null }>) => void;
  /** Opens a script of a project in the code editor of that project, at its path there; without it, no script is edited from the tab. */
  onEditScript?: (project: WorkspaceProject, path: string) => void;
  /** The providers a run can use, for the form of the Agent chip of Run…; none hides it. */
  providers?: readonly RunProvider[];
  /** Reads what the form of the Agent chip offers for a project; null where nothing reads it. */
  sessionSettings?: ((projectId: string) => SessionSettingsApi | null) | null;
}) {
  const { t } = useShellLanguage();
  const hub = useMemo(() => epoch ? createWorkflowsHub(api, epoch) : null, [api, epoch]);
  const [project, setProject] = useState<string | null>(() => projectId && projects.some(candidate => candidate.id === projectId) ? projectId : projects[0]?.id ?? null);
  const [scripts, setScripts] = useState<WorkflowsList<WorkflowScriptItem> | null>(null);
  const [query, setQuery] = useState("");
  const [reading, setReading] = useState(0);
  const [running, setRunning] = useState<WorkflowScriptItem | null>(null);
  const [showing, setShowing] = useState<WorkflowScriptItem | null>(null);
  const openFile = useContext(MarkdownLinksContext);
  const chosen = projects.find(candidate => candidate.id === project) ?? null;
  // A script of the project opens in the code editor of the project; one of yours, as a file of this computer. A sample is not edited.
  const editOf = (script: WorkflowScriptItem): (() => void) | null => {
    if (script.scope === "project") {
      const path = chosen && onEditScript ? projectScriptPath(script, chosen.path) : null;
      return chosen && path ? () => onEditScript!(chosen, path) : null;
    }
    return script.scope === "global" && openFile ? () => openFile(script.path, null) : null;
  };
  const deleteOf = (script: WorkflowScriptItem): (() => Promise<string | null>) | null => script.scope !== "project" && script.scope !== "global" || !hub ? null
    : async () => {
      const outcome = await hub.delete(script.name, script.scope as "project" | "global", script.scope === "project" ? project : null);
      if (!outcome.ok) return outcome.message ?? "";
      setReading(value => value + 1);
      return null;
    };
  const runs = useRuns(hub, visible, reading);
  const ordered = useMemo(() => sortRuns(runs?.items ?? []), [runs]);
  const now = Math.max(useNow(visible, ordered.some(run => run.state === "running")), Date.now());
  const byId = useMemo(() => new Map(sessions.map(session => [session.id, session])), [sessions]);
  // The samples show until the user hides them, a choice the window keeps.
  const [samples, setSamples] = useState(() => restoreSamplesShown(() => localStorage.getItem(samplesShownKey)));
  const toggleSamples = () => setSamples(current => {
    try { localStorage.setItem(samplesShownKey, String(!current)); } catch { /* Kept for this window only. */ }
    return !current;
  });
  const sampleCount = scripts?.items.filter(script => script.scope === "builtin").length ?? 0;
  const shown = useMemo(() => filterScripts((scripts?.items ?? []).filter(script => samples || script.scope !== "builtin"), query), [scripts, query, samples]);
  // The library is read again when the tab shows, for another project, or on Reload.
  useEffect(() => {
    if (!hub || !visible) return;
    const abort = new AbortController();
    void hub.scripts(project, abort.signal).then(value => { if (!abort.signal.aborted && value) setScripts(value); });
    return () => abort.abort();
  }, [hub, visible, project, reading]);

  if (epoch === null || scripts?.available === false) return <div className="workflows" onPointerDown={onActivate}><p className="automation-empty">{t("Workflows are unavailable in this window.")}</p></div>;
  if (!scripts) return <div className="workflows" onPointerDown={onActivate}><p className="automation-empty"><ActivitySpinner size={16} /></p></div>;
  return <div className="workflows" onPointerDown={onActivate}>
    <header className="automations-header workflows-header">
      <span className="automations-emblem workflows-emblem"><AppIcon name="workflow" size={22} /></span>
      <div><h2>{t("Workflows")}</h2><p>{t("Scripts that run tools and sessions, step by step.")}</p></div>
      <HTMLSelect className="workflows-project" value={project ?? ""} aria-label={t("Project")} onChange={event => setProject(event.target.value || null)}>
        <option value="">{t("No project")}</option>
        {projects.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name}</option>)}
      </HTMLSelect>
      <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} aria-label={t("Reload")} title={t("Reload")} onClick={() => setReading(value => value + 1)} />
    </header>
    <div className="automations-body">
      <section className="automations-main workflow-library" aria-label={t("Library")}>
        <div className="automation-list-head workflow-library-head">
          <h3>{t("Library")}<span className="count">{scripts.items.length}</span></h3>
          {sampleCount > 0 && <Button size="small" variant="minimal" className="workflow-samples-toggle" aria-pressed={samples}
            icon={<AppIcon name={samples ? "hide" : "eye"} size={13} />} onClick={toggleSamples}>{t(samples ? "Hide samples" : "Show samples")}</Button>}
          <InputGroup size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query} placeholder={t("Search scripts")}
            aria-label={t("Search scripts")} onChange={event => setQuery(event.target.value)} />
        </div>
        {/* Workflows are written by agents, not by hand: how to get one. */}
        <p className="workflow-library-how">{t("Agents write workflows: ask for one in a session, or click Save as script… on the card of a run that went well.")}</p>
        {query && shown.length === 0 ? <p className="automation-empty">{t("No script matches.")}</p>
          : libraryScopes.filter(part => (part.scope !== "project" || chosen) && (part.scope !== "builtin" || samples)).map(part => {
            const items = shown.filter(script => script.scope === part.scope);
            if (query && items.length === 0) return null;
            const caption = t(part.caption, { project: chosen?.name ?? "" });
            // One line for a part: its name, its count, where its scripts are, and that it has none yet.
            return <section key={part.scope} className="workflow-scope-section" data-scope={part.scope} aria-label={t(part.title)}>
              <div className="workflow-scope-line">
                <h4>{t(part.title)}<span className="count">{items.length}</span></h4>
                <span className="workflow-scope-caption" title={caption}>{caption}</span>
                {items.length === 0 && <span className="workflow-scope-empty">{t("None yet.")}</span>}
              </div>
              {items.length > 0 && <ul className="workflow-cards">{items.map(script => <ScriptCard key={`${script.scope}:${script.name}`} script={script}
                onShow={() => setShowing(script)} onRun={() => setRunning(script)} onEdit={editOf(script)} onDelete={deleteOf(script)} />)}</ul>}
            </section>;
          })}
      </section>
      <aside className="automations-side">
        <section className="automation-detail" aria-label={t("Runs")}>
          <h4>{t("Runs")}</h4>
          {runs === null ? <p className="automation-empty"><ActivitySpinner size={13} /></p> : <RunList runs={ordered} sessions={byId} now={now}
            onOpen={run => onOpenRun ? onOpenRun({ runId: run.id, title: run.title }) : onOpenSession(run.sessionId)} />}
        </section>
      </aside>
    </div>
    {showing && hub && <ScriptWindow hub={hub} script={showing} projectId={project} facts={<ScriptFacts script={showing} />} onClose={() => setShowing(null)} onEdit={editOf(showing)}
      onRun={showing.problem ? null : () => { setRunning(showing); setShowing(null); }} />}
    {running && hub && <RunScriptWindow hub={hub} script={running} projects={projects} projectId={project} providers={providers} sessionSettings={sessionSettings} onClose={() => setRunning(null)}
      onStarted={sessionId => { setRunning(null); setReading(value => value + 1); onOpenSession(sessionId); }} />}
  </div>;
}
