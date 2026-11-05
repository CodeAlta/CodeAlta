import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { Alert, Button, Callout, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, SegmentedControl, Switch, Tag } from "@blueprintjs/core";
import type { AutomationItem, AutomationRunItem, AutomationTeamItem, WorkspaceProject, WorkspaceSession } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { ModelIcon } from "../ProviderIcon";
import { TriggerIcon } from "./TriggerIcon";
import { sessionTime, timelineTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { AutomationEditor } from "./AutomationEditor";
import { automationTemplates, describeAllowedTool, describeAutonomy, describeTrigger, describeTriggers, emptyForm, filterAutomations, formOf, placementLabel, placementProblem, placementWait, runStatusLabel, runTone, runTriggerLabel, runsWithFacts,
  timelinePosition, timelineRows, triggerTone, waitsToBeAllowed,
  type AutomationForm, type AutomationScope, type AutomationTemplate } from "./automations";
import type { AutomationsHub } from "./automationsHub";
import type { RunProvider } from "../workItems/runsWith";
import { automationProjects, machineRunProviders } from "../machines/machineProviderNames";
import { useRemoteChoices } from "../machines/RunOnChip";
import { showHeldStartToast } from "../fleet/FleetApprovals";

const day = 24 * 60 * 60 * 1000;

function useNow(visible: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!visible) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, [visible]);
  return now;
}

function RunMark({ status }: { status: string }) {
  const { t } = useShellLanguage();
  const tone = runTone(status);
  const label = t(runStatusLabel(status));
  return <span className="automation-run-mark" data-tone={tone} role="img" aria-label={label} title={label}>
    {tone === "running" ? <ActivitySpinner size={12} /> : <AppIcon name={tone === "ok" ? "check" : tone === "failed" ? "error" : tone === "held" ? "usage" : "minus"} size={13} />}</span>;
}

/** The runs of the next day on one line: a mark for each time a schedule is due, the nearest at the left. */
function Timeline({ upcoming, items, now, selected, onSelect }: {
  upcoming: readonly Readonly<{ automationId: string; at: string }>[]; items: readonly AutomationItem[]; now: number; selected: string | null; onSelect: (id: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const hour = (value: number) => {
    try { return new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(new Date(value)); }
    catch { return ""; }
  };
  // A tick every four hours of the clock, the first one after now.
  const ticks: number[] = [];
  const first = new Date(now); first.setMinutes(0, 0, 0);
  for (let at = first.getTime() + 60 * 60 * 1000; at < now + day; at += 60 * 60 * 1000) if (new Date(at).getHours() % 4 === 0) ticks.push(at);
  const names = new Map(items.map(item => [item.id, item]));
  const position = (at: string) => `${timelinePosition(at, now, now + day) * 100}%`;
  return <section className="automation-timeline" aria-label={t("Next 24 hours")}>
    <h3>{t("Next 24 hours")}</h3>
    <div className="automation-timeline-track">
      <span className="automation-timeline-now" title={t("Now")} />
      {ticks.map(at => <span key={at} className="automation-timeline-tick" style={{ left: position(new Date(at).toISOString()) }}><i />{hour(at)}</span>)}
      {timelineRows(upcoming).map(row => {
        const item = names.get(row.automationId);
        if (!item) return null;
        const tone = triggerTone(item.triggers[0]?.type);
        // An automation that runs all along is one band, not a mark for each of its runs.
        if (row.dense) {
          const label = `${item.name} · ${describeTriggers(item.triggers, t, locale)}`;
          return <button key={row.automationId} type="button" className="automation-timeline-band" data-file-tone={tone} data-selected={selected === item.id || undefined}
            style={{ left: position(row.times[0]), right: `calc(100% - ${position(row.times[row.times.length - 1])})` }} title={label} aria-label={label} onClick={() => onSelect(item.id)} />;
        }
        return row.times.map(at => {
          const label = `${item.name} · ${timelineTime(at, locale).label}`;
          return <button key={`${row.automationId}:${at}`} type="button" className="automation-timeline-mark" data-file-tone={tone} data-selected={selected === item.id || undefined}
            style={{ left: position(at) }} title={label} aria-label={label} onClick={() => onSelect(item.id)} />;
        });
      })}
    </div>
  </section>;
}

function ScopeTag({ item }: { item: AutomationItem }) {
  const { t } = useShellLanguage();
  const chat = item.projectId === null && !item.projectFolder;
  return <span className="automation-scope" data-kind={chat ? "chat" : "project"} title={chat ? t("Runs as a chat, in no project") : item.projectFolder ?? undefined}>
    <AppIcon name={chat ? "chat" : "folder"} size={13} /><span>{chat ? t("Chat") : item.projectName ?? item.projectFolder}</span></span>;
}

function AutomationCard({ item, paused, selected, busy, now, onSelect, onRun, onEdit, onToggle, onAllow, onStartPlaced, onDuplicate, onDelete }: {
  item: AutomationItem; paused: boolean; selected: boolean; busy: boolean; now: number;
  onSelect: () => void; onRun: () => void; onEdit: () => void; onToggle: (enabled: boolean) => void; onAllow: () => void; onStartPlaced: () => void; onDuplicate: () => void; onDelete: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const [starting, setStarting] = useState(false);
  const tone = triggerTone(item.triggers[0]?.type);
  const next = item.nextRunAt ? sessionTime(item.nextRunAt, locale, now) : null;
  const last = item.lastRun ? sessionTime(item.lastRun.startedAt, locale, now) : null;
  // What keeps it from running as defined, or its triggers from seeing their repository, or its machine from having it.
  const problem = item.problem ?? placementProblem(item) ?? (paused ? null : item.watchProblem);
  const waits = !item.problem && waitsToBeAllowed(item);
  // The machine it left may still run its copy: it is copied to its new machine once that one takes it away.
  const handoff = item.problem ? null : placementWait(item);
  const placed = placementLabel(item, t);
  const state = problem ?? (item.running ? t("Running") : item.triggers.length === 0 ? t("Run it when you need it")
    : waits ? t("Waits for you to allow it") : handoff !== null ? t("Waits for the machine it left") : !item.enabled ? t("Disabled")
    : placed ?? (paused ? t("Paused") : next ? t("Next {time}", { time: next.label })
    : item.repository ? t("Watches {repository}", { repository: item.repository }) : t("Waits for its event")));
  const menu = <Menu>
    <MenuItem icon={<AppIcon name="edit" size={15} />} text={t("Edit…")} onClick={onEdit} />
    <MenuItem icon={<AppIcon name="copy" size={15} />} text={t("Duplicate")} onClick={onDuplicate} />
    <MenuDivider />
    <MenuItem icon={<AppIcon name="trash" size={15} />} intent="danger" text={t("Delete…")} onClick={onDelete} />
  </Menu>;
  return <li className="automation-card" data-selected={selected || undefined} data-off={!item.enabled || undefined}>
    <button type="button" className="automation-card-main" aria-pressed={selected} onClick={onSelect}>
      <span className="automation-card-glyph" data-file-tone={tone}>{item.running ? <ActivitySpinner size={16} /> : <TriggerIcon type={item.triggers[0]?.type} size={18} />}</span>
      <span className="automation-card-text">
        <strong>{item.name}</strong>
        <span className="automation-card-when">{describeTriggers(item.triggers, t, locale)}</span>
      </span>
    </button>
    {item.triggers.length > 0 && <Switch className="automation-card-switch" checked={item.enabled} disabled={busy} aria-label={t("Enabled")} title={t(item.enabled ? "Enabled" : "Disabled")}
      onChange={event => onToggle(event.currentTarget.checked)} />}
    <div className="automation-card-meta"><ScopeTag item={item} />
      {placed && <span className="automation-chip" title={item.placement?.message ?? undefined}><AppIcon name="server" size={12} /><span>{placed}</span></span>}
      {item.provider && <span className="automation-chip"><ModelIcon modelId={item.model} providerKey={item.provider} size={12} /><span>{item.model ?? item.provider}</span></span>}</div>
    <div className="automation-card-foot">
      <span className="automation-card-state" data-problem={problem ? true : undefined} title={problem ?? (handoff || undefined) ?? next?.title}>{state}</span>
      {item.lastRun && <span className="automation-card-last" title={`${t(runStatusLabel(item.lastRun.status))} · ${last?.title ?? ""}`}><RunMark status={item.lastRun.status} />{last?.label}</span>}
      <span className="automation-card-actions">
        {waits && <Button size="small" intent="primary" disabled={busy} title={t("Let its triggers start it")} onClick={onAllow}>{t("Allow")}</Button>}
        {handoff !== null && <span className="automation-start-anchor">
          <Button size="small" intent="warning" disabled={busy} onClick={() => setStarting(true)}>{t("Start anyway…")}</Button>
          {starting && <ConfirmPopover always placement="bottom-end" intent="warning" busy={busy} subject={item.name} confirmLabel={t("Start anyway")}
            title={item.runsOn ? t("Start it on {machine} now?", { machine: item.placement?.machineName ?? t("Unavailable machine") }) : t("Start it on this computer now?")}
            detail={t("The machine it left may still run its copy until it answers: both machines may run it meanwhile.")}
            onConfirm={() => { setStarting(false); onStartPlaced(); }} onCancel={() => setStarting(false)} />}
        </span>}
        <Button variant="minimal" size="small" intent="primary" disabled={busy || !!item.problem} icon={<AppIcon name="play" size={14} />} aria-label={t("Run now")} title={t("Run now")} onClick={onRun} />
        <PopoverNext content={menu} placement="bottom-end"><Button variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("Actions for {title}", { title: item.name })} title={t("More")} /></PopoverNext>
      </span>
    </div>
  </li>;
}

function RunList({ runs, sessions, now, named, busy, onOpen, onTakeOver }: {
  runs: readonly AutomationRunItem[]; sessions: ReadonlyMap<string, WorkspaceSession>; now: number;
  /** Whether each run says which automation it is of: in the list of all of them. */
  named: boolean; busy: ReadonlySet<string>; onOpen: (sessionId: string, projectId: string | null) => void;
  /** Takes a run that waits for a person over. */
  onTakeOver: (run: AutomationRunItem) => void;
}) {
  const { t, locale } = useShellLanguage();
  if (runs.length === 0) return <p className="automation-empty">{t("No run yet.")}</p>;
  return <ul className="automation-runs">
    {runs.map(run => {
      const session = run.sessionId ? sessions.get(run.sessionId) : undefined;
      // The session of a run on another machine opens from the run: the window reads that machine's sessions again when it is not listed yet.
      const remote = !session && run.sessionId && run.machineId ? run.sessionId : null;
      const when = sessionTime(run.startedAt, locale, now);
      // Among the runs of all, a run says whose it is; among those of one automation, how it ended.
      const status = t(runStatusLabel(run.status));
      const body = <>
        <RunMark status={run.status} />
        <span className="automation-run-text">
          <strong>{named ? run.name ?? t("Automation") : status}</strong>
          <span title={run.output ?? undefined}>{[named ? status : null, t(runTriggerLabel(run.trigger)), run.detail, run.message, run.reply].filter(Boolean).join(" · ")}</span>
        </span>
        <time dateTime={when.dateTime} title={when.title}>{when.label}</time>
        {(session || remote) && <AppIcon name="chevronRight" size={14} />}
      </>;
      // A run that waits for a person is taken over from here; the calls a try recorded instead of making them are listed under it.
      return <li key={run.id} data-tone={runTone(run.status)}>
        {session ? <button type="button" className="automation-run" title={t("Open the session")} onClick={() => onOpen(session.id, session.projectId)}>{body}</button>
          : remote ? <button type="button" className="automation-run" title={t("Open the session")} onClick={() => onOpen(remote, run.projectId)}>{body}</button>
          : <div className="automation-run">{body}</div>}
        {run.status === "waiting" && <div className="automation-run-waits">
          <span>{t(run.machineId ? "The run waits for you: open its session on its machine to answer it." : "The run waits for you: answer it in its session, or take it over.")}</span>
          <Button size="small" intent="primary" disabled={busy.has(run.id)} loading={busy.has(run.id)} onClick={() => onTakeOver(run)}>{t("Take over")}</Button>
        </div>}
        {run.calls && run.calls.length > 0 && <ul className="automation-run-calls">
          {run.calls.map((call, index) => <li key={index} title={call}>{t("Recorded, not made: {call}", { call })}</li>)}
        </ul>}
      </li>;
    })}
  </ul>;
}

function AutomationDetail({ hub, item, paused, busy, taking, now, sessions, epoch, providers, onRun, onTry, onEdit, onAllow, onOpen, onTakeOver }: {
  hub: AutomationsHub; item: AutomationItem; paused: boolean; busy: boolean; taking: ReadonlySet<string>; now: number; sessions: ReadonlyMap<string, WorkspaceSession>;
  epoch: string | null | undefined; providers: readonly RunProvider[];
  onRun: () => void; onTry: () => void; onEdit: () => void; onAllow: () => void; onOpen: (sessionId: string, projectId: string | null) => void;
  onTakeOver: (run: AutomationRunItem) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [runs, setRuns] = useState<readonly AutomationRunItem[] | null>(null);
  // Its runs are read again when it is shown, runs, or stops running.
  const stamp = `${item.id}:${item.running}:${item.lastRun?.id ?? ""}:${item.lastRun?.status ?? ""}`;
  useEffect(() => {
    let current = true;
    void hub.runs(item.id).then(value => { if (current) setRuns(value); });
    return () => { current = false; };
  }, [hub, stamp]);
  const next = item.nextRunAt ? timelineTime(item.nextRunAt, locale) : null;
  const waits = !item.problem && waitsToBeAllowed(item);
  // Its provider as the machine that runs it names it, else as this computer does.
  const machine = useRemoteChoices(epoch, item.runsOn ?? null, null, undefined, true).choices;
  const providerName = (item.runsOn ? machineRunProviders(machine) : providers)?.find(provider => provider.id === item.provider)?.name ?? null;
  const runsWith = runsWithFacts(item, t, providerName);
  const placed = placementLabel(item, t);
  const notCopied = !item.problem ? placementProblem(item) : null;
  return <section className="automation-detail" aria-label={item.name}>
    <header>
      <span className="automation-card-glyph" data-file-tone={triggerTone(item.triggers[0]?.type)}><TriggerIcon type={item.triggers[0]?.type} size={20} /></span>
      <div><h3>{item.name}</h3><ScopeTag item={item} /></div>
      <Button intent="primary" disabled={busy || !!item.problem} loading={busy} icon={<AppIcon name="play" size={14} />} onClick={onRun}>{t("Run now")}</Button>
      {/* A try of an automation with autonomy makes none of the calls it would make by itself: it records them on its run. */}
      <Button icon={<AppIcon name="edit" size={14} />} onClick={onEdit}>{t("Edit")}</Button>
    </header>
    {item.problem && <Callout intent="warning" compact role="alert">{item.problem}</Callout>}
    {notCopied && <Callout intent="warning" compact role="alert">{notCopied}</Callout>}
    {!item.problem && !paused && item.watchProblem && <Callout intent="warning" compact role="alert">{item.watchProblem}</Callout>}
    {waits && <Callout intent="primary" compact className="automation-allow">
      <span>{t(item.script && item.storeProjectId === null ? "This automation runs a script that changed since you allowed it. Its triggers start it once you allow it again."
        : "This automation came with the project. Its triggers start it once you allow it.")}</span>
      <Button size="small" intent="primary" disabled={busy} onClick={onAllow}>{t("Allow")}</Button>
    </Callout>}
    <dl className="automation-facts">
      <dt>{t("Starts")}</dt>
      <dd>{item.triggers.length === 0 ? t("When you run it") : <ul>{item.triggers.map((trigger, index) =>
        <li key={index}><span data-file-tone={triggerTone(trigger.type)}><TriggerIcon type={trigger.type} size={13} /></span>{describeTrigger(trigger, t, locale, true)}</li>)}</ul>}</dd>
      {/* It runs on its machine, by its triggers there: this computer shows where and whether the machine has it as it is. */}
      {placed && <><dt>{t("Runs on")}</dt>
        <dd title={item.placement?.message ?? undefined}>{item.placement?.state === "copied"
          ? t("Copied to {machine}", { machine: item.placement.machineName ?? t("Unavailable machine") }) : [placed, item.placement?.message].filter(Boolean).join(" · ")}</dd></>}
      {item.triggers.length > 0 && !placed && <><dt>{t("Next run")}</dt>
        <dd>{waits ? t("Waits for you to allow it") : !item.enabled ? t("Disabled") : paused ? t("Paused") : next?.label ?? t("Waits for its event")}</dd></>}
      {item.repository && <><dt>{t("Repository")}</dt><dd>{item.repository}</dd></>}
      {/* What its sessions run with besides the defaults, which an automation that came with a project shows before it is allowed. */}
      {runsWith.length > 0 && <><dt>{t("Runs with")}</dt><dd>{runsWith.join(" · ")}</dd></>}
      {/* What its runs do without a person, what its tools are held to, who approves; and its try, which makes none of the calls.
          On another machine, the calls a try records stay in the session of the run there: the server carries no content. */}
      {item.autonomy && <><dt>{t("Autonomy")}</dt><dd className="automation-autonomy-fact">
        <span className="automation-autonomy-said">{describeAutonomy(item.autonomy, t)}</span>
        {(item.autonomy.tools ?? []).map(tool => { const said = describeAllowedTool(tool, t); return said && <span key={tool.name}><code>{tool.name}</code> {said}</span>; })}
        {item.autonomy.approvers === "oncall" && <span>{t("Its approvals go to the person on call of its team.")}</span>}
        <Button size="small" icon={<AppIcon name="play" size={12} />} disabled={busy || !!item.problem} onClick={onTry}
          title={t(item.runsOn ? "A try on its machine replaces the tools it calls by itself with fakes: their calls are in the session of the run there, not made."
            : "A try replaces the tools it calls by itself with fakes: their calls are recorded on the run, not made.")}>{t("Try it (dry run)")}</Button>
      </dd></>}
      <dt>{t("Stored in")}</dt><dd className="automation-file" title={item.file}>{item.file}</dd>
    </dl>
    {item.script ? <><h4>{t("Script")}</h4>
      <pre className="automation-prompt">{item.scriptArguments ? `${item.script}(${item.scriptArguments})` : item.script}</pre></>
      : <><h4>{t("Prompt")}</h4><pre className="automation-prompt">{item.prompt}</pre></>}
    <h4>{t("Runs")}</h4>
    {runs === null ? <p className="automation-empty"><ActivitySpinner size={13} /></p> : <RunList runs={runs} sessions={sessions} now={now} named={false} busy={taking} onOpen={onOpen} onTakeOver={onTakeOver} />}
  </section>;
}

function Templates({ onPick, chatOnly }: { onPick: (template: AutomationTemplate) => void; chatOnly: boolean }) {
  const { t, locale } = useShellLanguage();
  return <section className="automation-templates" aria-label={t("Start from a template")}>
    <h3>{t("Start from a template")}</h3>
    <ul>{automationTemplates.map(template => <li key={template.key}>
      <button type="button" className="automation-template" disabled={chatOnly && template.project}
        title={chatOnly && template.project ? t("Open a project first: this one runs in a repository.") : undefined} onClick={() => onPick(template)}>
        <span className="automation-card-glyph" data-file-tone={template.tone}><AppIcon name={template.icon} size={18} /></span>
        <strong>{t(template.name)}</strong>
        <span>{t(template.description)}</span>
        <Tag minimal round>{describeTriggers(template.triggers, t, locale)}</Tag>
      </button></li>)}
    </ul>
  </section>;
}

/**
 * The tab of the automations: what they are, when they run next, what they started, and the templates a new
 * one starts from. Everything it shows comes from the host; it asks again when the host says something changed.
 */
export function AutomationsPanel({ hub, projects, sessions, projectId, providers, epoch, visible, focus, onFocused, onActivate, onOpenSession }: {
  hub: AutomationsHub;
  /** The projects an automation can run in. */
  projects: readonly WorkspaceProject[];
  sessions: readonly WorkspaceSession[];
  /** The project selected in the window, where a new automation runs unless the user says otherwise. */
  projectId: string | null;
  /** The enabled providers. */
  providers: readonly RunProvider[];
  epoch: string | null | undefined;
  visible: boolean;
  /** The automation to show, asked from elsewhere in the window. */
  focus?: string | null;
  onFocused?: () => void;
  onActivate: () => void;
  onOpenSession: (sessionId: string, projectId: string | null) => void;
}) {
  const { t } = useShellLanguage();
  // The projects of this computer: the row of the Explorer that stands for a machine is not one an automation runs in.
  const ownProjects = useMemo(() => automationProjects(projects), [projects]);
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  // What the host just said happened is not in the future of a clock read half a minute ago.
  const now = Math.max(useNow(visible), Date.now());
  const [selected, setSelected] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [scope, setScope] = useState<AutomationScope>("all");
  const [editing, setEditing] = useState<AutomationForm | null>(null);
  const [deleting, setDeleting] = useState<AutomationItem | null>(null);
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [notice, setNotice] = useState<string | null>(null);
  // The automations of the teams this person maintains that another maintainer gave, read when the page shows.
  const [teamItems, setTeamItems] = useState<readonly AutomationTeamItem[]>([]);
  const [teamRead, setTeamRead] = useState(0);
  const byId = useMemo(() => new Map(sessions.map(session => [session.id, session])), [sessions]);
  const shown = useMemo(() => filterAutomations(state.items, query, scope), [state.items, query, scope]);
  const current = state.items.find(item => item.id === selected) ?? null;
  useEffect(() => { if (visible) void hub.refresh(); }, [hub, visible]);
  useEffect(() => {
    if (!visible) return;
    const abort = new AbortController();
    void hub.teamAutomations(abort.signal).then(items => { if (!abort.signal.aborted) setTeamItems(items); });
    return () => abort.abort();
  }, [hub, visible, epoch, teamRead]);
  useEffect(() => {
    if (!focus || !state.items.some(item => item.id === focus)) return;
    setSelected(focus); setQuery(""); setScope("all");
    onFocused?.();
  }, [focus, state.items]);

  const during = async <T,>(id: string, work: () => Promise<T>) => {
    setBusy(value => new Set(value).add(id));
    try { return await work(); }
    finally { setBusy(value => { const next = new Set(value); next.delete(id); return next; }); }
  };
  async function run(id: string) {
    setNotice(null);
    const outcome = await during(id, () => hub.run(id));
    setSelected(id);
    if (!outcome.ok) { setNotice(outcome.message ?? t("The automation did not start.")); return; }
    // A budget of its machine holds it: it waits for Continue anyway, offered beside what says so.
    if (outcome.run?.status === "held") {
      const item = state.items.find(candidate => candidate.id === id);
      if (epoch) showHeldStartToast({ epoch, runId: outcome.run.id, say: t, message: t("{name} waits for Continue anyway on {machine}: a budget of the machine is reached.",
        { name: item?.name ?? outcome.run.name ?? t("Automation"), machine: item?.placement?.machineName ?? t("Unavailable machine") }) });
      return;
    }
    if (outcome.run?.sessionId) onOpenSession(outcome.run.sessionId, outcome.run.projectId ?? null);
  }
  async function allow(item: AutomationItem) {
    setNotice(null);
    const outcome = await during(item.id, () => hub.allow(item.id));
    if (!outcome.ok) setNotice(outcome.message ?? t("The automation could not be saved."));
  }
  async function startPlaced(item: AutomationItem) {
    setNotice(null);
    const outcome = await during(item.id, () => hub.startPlaced(item.id));
    if (!outcome.ok) setNotice(outcome.message ?? t("The automation could not be saved."));
  }
  async function toggle(item: AutomationItem, enabled: boolean) {
    setNotice(null);
    const outcome = await during(item.id, () => hub.setEnabled(item.id, enabled));
    if (!outcome.ok) setNotice(outcome.message ?? t("The automation could not be saved."));
  }
  async function dryRun(id: string) {
    setNotice(null);
    const outcome = await during(id, () => hub.tryRun(id, -1, null));
    setSelected(id);
    if (!outcome.ok && !outcome.run) { setNotice(outcome.message ?? t("The automation did not start.")); return; }
    if (outcome.run?.sessionId) onOpenSession(outcome.run.sessionId, outcome.run.projectId ?? null);
  }
  // The session of a run taken over opens: on this computer from the answer, on another machine from the run.
  async function takeOver(run: AutomationRunItem) {
    setNotice(null);
    const outcome = await during(run.id, () => hub.takeOver(run.automationId, run.id));
    if (!outcome.ok) { setNotice(outcome.message ?? t("The run could not be taken over.")); return; }
    const sessionId = outcome.sessionId ?? run.sessionId;
    if (sessionId) onOpenSession(sessionId, run.projectId);
  }
  // An automation of a team given by another maintainer: taken here as its machine holds it, then changed in the editor.
  async function openTeam(item: AutomationTeamItem) {
    setNotice(null);
    const outcome = await during(item.id, () => hub.openTeamAutomation(item));
    setTeamRead(value => value + 1);
    if (!outcome.ok) { setNotice(outcome.message ?? t("The automation could not be opened.")); return; }
    const taken = hub.getSnapshot().items.find(candidate => candidate.id === item.id);
    setSelected(item.id);
    if (taken) setEditing(formOf(taken));
  }
  const create = (form: AutomationForm) => setEditing(form);
  const fromTemplate = (template: AutomationTemplate) => create({ ...emptyForm(template.project ? projectId : null), name: t(template.name), prompt: template.prompt,
    script: template.script ?? "", scriptArguments: template.scriptArguments ?? "", triggers: template.triggers });

  if (!state.loaded) return <div className="automations" onPointerDown={onActivate}><p className="automation-empty"><ActivitySpinner size={16} /></p></div>;
  if (!state.available) return <div className="automations" onPointerDown={onActivate}><p className="automation-empty">{t("Automations are unavailable in this window.")}</p></div>;
  return <div className="automations" onPointerDown={onActivate} data-empty={state.items.length === 0 || undefined}>
    <header className="automations-header">
      <span className="automations-emblem"><AppIcon name="automation" size={22} /></span>
      <div><h2>{t("Automations")}</h2><p>{t("Prompts that start a session by themselves: on a schedule, on an event of the repository, or when you ask.")}</p></div>
      <Switch className="automations-pause" checked={!state.paused} label={t(state.paused ? "Paused" : "Running")} alignIndicator="end"
        title={t(state.paused ? "The triggers start nothing" : "The triggers start their automations")} onChange={event => void hub.setPaused(!event.currentTarget.checked)} />
      <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} aria-label={t("Reload")} title={t("Read the configuration files again")} onClick={() => void hub.refresh()} />
      <Button intent="primary" icon={<AppIcon name="plus" size={16} />} onClick={() => create(emptyForm(projectId))}>{t("New automation")}</Button>
    </header>
    {notice && <Callout intent="danger" compact role="alert" className="automations-notice">{notice}</Callout>}
    {state.faults.length > 0 && <Callout intent="warning" compact className="automations-notice" title={t("Not read as automations")}>
      <ul className="automation-faults">{state.faults.map((fault, index) => <li key={index}><code>{fault.key || "…"}</code><span>{fault.message}</span><small title={fault.file}>{fault.file}</small></li>)}</ul>
    </Callout>}
    <div className="automations-body">
      <div className="automations-main">
        {state.items.length === 0
          ? <section className="automations-hero">
              <h3>{t("Let CodeAlta do the routine")}</h3>
              <p>{t("Triage the issues every morning, review each pull request, draft the changelog on Fridays: write the prompt once and choose when it runs.")}</p>
              <Button intent="primary" large icon={<AppIcon name="plus" size={16} />} onClick={() => create(emptyForm(projectId))}>{t("New automation")}</Button>
            </section>
          : <>
            {state.upcoming.length > 0 && <Timeline upcoming={state.upcoming} items={state.items} now={now} selected={selected} onSelect={setSelected} />}
            <section className="automation-list" aria-label={t("Your automations")}>
              <div className="automation-list-head">
                <h3>{t("Your automations")}<span className="count">{state.items.length}</span></h3>
                <SegmentedControl size="small" value={scope} onValueChange={value => setScope(value as AutomationScope)}
                  options={[{ value: "all", label: t("All") }, { value: "projects", label: t("Projects") }, { value: "chats", label: t("Chats") }]} />
                <InputGroup size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query} placeholder={t("Search automations")}
                  aria-label={t("Search automations")} onChange={event => setQuery(event.target.value)} />
              </div>
              {shown.length === 0 ? <p className="automation-empty">{t("No automation matches.")}</p>
                : <ul className="automation-cards">{shown.map(item => <AutomationCard key={item.id} item={item} paused={state.paused} selected={item.id === selected}
                    busy={busy.has(item.id)} now={now} onSelect={() => setSelected(item.id === selected ? null : item.id)} onRun={() => void run(item.id)}
                    onEdit={() => setEditing(formOf(item))} onToggle={enabled => void toggle(item, enabled)} onAllow={() => void allow(item)}
                    onStartPlaced={() => void startPlaced(item)} onDuplicate={() => create({ ...formOf(item), id: null, name: t("{name} (copy)", { name: item.name }) })} onDelete={() => setDeleting(item)} />)}</ul>}
            </section>
          </>}
        {teamItems.length > 0 && <section className="automation-list automation-team-list" aria-label={t("Automations of your teams")}>
          <div className="automation-list-head"><h3>{t("Automations of your teams")}<span className="count">{teamItems.length}</span></h3></div>
          <ul className="automation-cards">{teamItems.map(item => <li key={`${item.linkId}|${item.id}|${item.machineId}`} className="automation-card automation-team-card" data-team-automation={item.id}>
            <div className="automation-team-text"><strong>{item.name}</strong>
              <span className="muted-text">{item.scope === "organization" ? t("The organization") : t("Team {name}", { name: item.scopeName })} · {item.machineName}</span></div>
            <Button size="small" icon={<AppIcon name="edit" size={14} />} disabled={busy.has(item.id)} onClick={() => void openTeam(item)}>{t("Open in my editor")}</Button>
          </li>)}</ul>
        </section>}
        <Templates onPick={fromTemplate} chatOnly={ownProjects.length === 0} />
      </div>
      {state.items.length > 0 && <aside className="automations-side">
        {current
          ? <AutomationDetail hub={hub} item={current} paused={state.paused} busy={busy.has(current.id)} taking={busy} now={now} sessions={byId} epoch={epoch} providers={providers}
              onRun={() => void run(current.id)} onTry={() => void dryRun(current.id)} onEdit={() => setEditing(formOf(current))} onAllow={() => void allow(current)}
              onOpen={onOpenSession} onTakeOver={run => void takeOver(run)} />
          : <section className="automation-detail" aria-label={t("Recent runs")}>
              <h4>{t("Recent runs")}</h4>
              <RunList runs={state.runs} sessions={byId} now={now} named busy={busy} onOpen={onOpenSession} onTakeOver={run => void takeOver(run)} />
            </section>}
      </aside>}
    </div>
    {editing && <AutomationEditor hub={hub} initial={editing} projects={ownProjects} providers={providers} epoch={epoch} onClose={() => setEditing(null)}
      onSaved={(id, start) => {
        setEditing(null); setSelected(id);
        if (start) void run(id);
      }} />}
    <Alert isOpen={!!deleting} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t("Delete")} canEscapeKeyCancel canOutsideClickCancel
      onCancel={() => setDeleting(null)} onConfirm={() => {
        const item = deleting;
        setDeleting(null);
        if (item) void during(item.id, () => hub.remove(item.id)).then(outcome => { if (!outcome.ok) setNotice(outcome.message ?? t("The automation could not be deleted.")); });
      }}>
      <p>{t("Delete the automation “{name}”? The sessions it started stay.", { name: deleting?.name ?? "" })}</p>
    </Alert>
  </div>;
}
