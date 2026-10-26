import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from "react";
import { Button, Callout, HTMLSelect, InputGroup, Menu, MenuItem, PopoverNext, SegmentedControl, Tab, Tabs, Tag } from "@blueprintjs/core";
import type { issues as issuesService, IssueFilterField, IssueFiltersResponse, IssueReadResponse, IssueRow, IssueSource, WorkspaceProject } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { BrandIcon } from "../BrandIcon";
import { serviceBrand } from "../brands";
import { AppWindow } from "../AppWindow";
import type { MessageKey } from "../localization";
import { MarkdownContent } from "../MarkdownContent";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { SessionSettingsChip, useSessionSettingsChoices } from "../sessionSettings/SessionSettingsChip";
import { defaultSessionSettings, startRequest } from "../sessionSettings/sessionSettings";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import { WorkStartButtons } from "../workItems/WorkItemCards";
import type { RunProvider } from "../workItems/runsWith";
import { startRefusal } from "../workItems/startWorkItem";
import type { WorkStart } from "../workItems/workItems";
import { allPartsLabel, choosePick, filterValueText, forgetPick, issueFilterLabel, joinValues, listedFilters, pickKey, readPicks, splitValues, trackerText, writePicks, issueFilters, issueIcon, issueKey, issueReference, issueStateLabel, issueTone, kindLabel, labelColors, sameList, shownPart, signInCommand, sourceKinds,
  type IssueFilter, type IssueKind, type IssueListKey } from "./issues";
import { viewKey, viewSourceLabel, viewSources } from "./issueFilterSettings";
import { IssueViewsWindow, type IssueViewsApi } from "./IssueViewsWindow";
import { MultiChoice } from "./MultiChoice";

const noProviders: readonly RunProvider[] = [];

/** The part of the host the tab uses; a test gives its own. */
export type IssuesApi = Pick<typeof issuesService, "sources" | "list" | "read" | "start" | "openLink"> & Partial<Pick<typeof issuesService, "filters" | "fields" | "views" | "saveView" | "renameView" | "removeView" | "selectView">>;

type Listing = Readonly<{ key: IssueListKey; loading: boolean; items: readonly IssueRow[]; more: boolean; problem: string | null; needsSignIn: boolean }>;
type Sources = Readonly<{ projectId: string; loading: boolean; sources: readonly IssueSource[] }>;
type Detail = Readonly<{ key: string; loading: boolean; value: IssueReadResponse | null }>;
/**
 * The filters of the current view of a tracker of a project, for the values chosen in the tab (`asked`): the last reply
 * (`value`, null until it comes), and the last one that gave them (`kept`), so that a request that failed leaves the tab
 * with the view it knew and the values chosen for it. The key names the view the tab last switched to as well.
 */
type Filters = Readonly<{ key: string; asked: string; loading: boolean; value: IssueFiltersResponse | null; kept: IssueFiltersResponse | null }>;
// A reply that gave the filters of the tracker: not a refusal, nor the one that says the tracker did not give them.
const gaveFilters = (reply: IssueFiltersResponse | null) => reply?.status === "ok" && (reply.filters.length > 0 || !reply.problem);
// What a request that did not end stands for: a reply without filters, which the tab does not list by.
const failedFilters: IssueFiltersResponse = { status: "failed", filters: [], selected: {}, active: [], pick: [], problem: null, views: [], current: null };

/**
 * The Issues tab: the issues and the pull requests of a project, from the service that hosts its repository
 * (GitHub, GitLab, Azure DevOps, Bitbucket) and from the trackers plugins add (Jira). One list at a time (open,
 * closed, merged, all) with what is typed as a filter, and one part of a tracker of several places (one Jira
 * project of a project that names several) when the user chooses it; an item is read on the right, opened in a
 * window or on the web, and a session can be started on it.
 */
export function IssuesPanel({ api, epoch, projects, projectId, visible, preferredStart, onActivate, onOpenSession, onStarted, onNotice, providers = noProviders, sessionSettings = null }: {
  api: IssuesApi;
  /** The enabled providers a new session can run with; none leaves out the Agent chip, and the host takes the defaults. */
  providers?: readonly RunProvider[];
  /** The catalogs of the form of the Agent chip of a new session of a project. */
  sessionSettings?: ((projectId: string) => SessionSettingsApi | null) | null;
  /** The epoch of the host; null when the window has no host that knows trackers. */
  epoch: string | null | undefined;
  projects: readonly WorkspaceProject[];
  /** The project selected in the window, which the tab shows first. */
  projectId: string | null;
  visible: boolean;
  /** The way of starting the user prefers: `worktree` or `session`. */
  preferredStart: string;
  onActivate: () => void;
  onOpenSession: (sessionId: string, projectId: string) => void;
  /** Offers a session started on an item while the user looked elsewhere, without showing it; it is opened when absent. */
  onStarted?: (sessionId: string, projectId: string, title: string) => void;
  /** Says something that went wrong, outside the tab. */
  onNotice: (message: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [scope, setScope] = useState<string | null>(projectId ?? projects[0]?.id ?? null);
  const [sources, setSources] = useState<Sources | null>(null);
  const [service, setService] = useState<string | null>(null);
  const [part, setPart] = useState<string | null>(null);
  const [trackerFilters, setTrackerFilters] = useState<Filters | null>(null);
  const [kind, setKind] = useState<IssueKind>("issue");
  const [filter, setFilter] = useState<IssueFilter>("open");
  const [typed, setTyped] = useState("");
  const [query, setQuery] = useState("");
  const [listing, setListing] = useState<Listing | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [expanded, setExpanded] = useState(false);
  // The items whose session starts, by project, tracker and item: several start at once, each in its own worktree.
  const [starting, setStarting] = useState<ReadonlySet<string>>(() => new Set());
  // The same, as of now: two clicks before a render start the item once.
  const startingNow = useRef(new Set<string>());
  const [revision, setRevision] = useState(0);
  const windowTitle = useId();
  const project = projects.find(candidate => candidate.id === scope) ?? null;
  // The project of the window is followed until the user chooses another one in the tab.
  const followed = useRef(true);
  useEffect(() => { if (followed.current && projectId && projects.some(candidate => candidate.id === projectId)) setScope(projectId); }, [projectId, projects]);
  useEffect(() => { if (!project && projects.length > 0) setScope(projects[0].id); }, [project, projects]);
  // What is typed is asked once the typing pauses.
  useEffect(() => { const timer = setTimeout(() => setQuery(typed.trim()), 350); return () => clearTimeout(timer); }, [typed]);

  const available = epoch !== null && epoch !== undefined;
  // What a session started on an item runs with: the Agent chip of the composer, the defaults until the user changes them.
  const [runSettings, setRunSettings] = useState(defaultSessionSettings);
  const settingsApi = useMemo(() => project && sessionSettings ? sessionSettings(project.id) : null, [project, sessionSettings]);
  const runChoices = useSessionSettingsChoices(settingsApi, providers, null, runSettings);
  useEffect(() => {
    if (!available || !visible || !project) return;
    const controller = new AbortController();
    setSources(current => current?.projectId === project.id ? { ...current, loading: true } : { projectId: project.id, loading: true, sources: [] });
    void api.sources({ expectedEpoch: epoch, projectId: project.id }, { signal: controller.signal }).then(
      reply => { if (!controller.signal.aborted) setSources({ projectId: project.id, loading: false, sources: reply.status === "ok" ? reply.sources : [] }); },
      () => { if (!controller.signal.aborted) setSources({ projectId: project.id, loading: false, sources: [] }); });
    return () => controller.abort();
  }, [api, epoch, available, visible, project?.id, revision]);

  const known = sources && project && sources.projectId === project.id ? sources.sources : [];
  const source = known.find(candidate => candidate.service === service) ?? known[0] ?? null;
  const kinds = sourceKinds(source);
  const shownKind = kinds.includes(kind) ? kind : kinds[0] ?? "issue";
  const filters = issueFilters(shownKind);
  const shownFilter = filters.includes(filter) ? filter : "open";
  const parts = source?.parts ?? [];
  const listedPart = shownPart(source, part);

  // The filters of the current view of the tracker: the tab lists by them and names them under its toolbar. The values
  // chosen for the filters the view lets the tab change are kept per view, and asked with. A view chosen, made, renamed
  // or removed starts them anew: nothing of the view before is shown, asked with or kept for the next one.
  const [viewChanges, setViewChanges] = useState(0);
  const filtersKey = project && source ? `${project.id}\n${source.service}\n${viewChanges}` : null;
  const filtering = !!api.filters && shownKind === "issue";
  // The view and the filters it lets the tab change are those of the last reply that gave them: a request with values the host
  // refuses or fails on does not make them unknown, else the values would be dropped, asked again and refused again.
  const lastInfo = filtering && trackerFilters?.key === filtersKey ? trackerFilters.kept : null;
  const picksKey = project && source && lastInfo?.current ? pickKey(project.id, source.service, lastInfo.current) : null;
  const [picks, setPicks] = useState<Readonly<{ key: string; values: Readonly<Record<string, string>> }> | null>(null);
  const keptPicks = useMemo(() => picksKey === null ? {} : readPicks(picksKey), [picksKey]);
  const storedPicks = picks?.key === picksKey ? picks.values : keptPicks;
  // Only the filters the view lets the tab change are asked with a choice: another filter would take it as a value of its own.
  const sentPicks = Object.fromEntries(Object.entries(storedPicks).filter(([id]) => lastInfo?.pick.includes(id)));
  const picksText = JSON.stringify(Object.entries(sentPicks).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
  useEffect(() => {
    if (!available || !visible || !filtering || !filtersKey || !project || !source || !api.filters) return;
    const controller = new AbortController();
    const asked = { ...sentPicks }, askedKey = picksKey;
    setTrackerFilters(current => current?.key === filtersKey ? { ...current, loading: true } : { key: filtersKey, asked: picksText, loading: true, value: null, kept: null });
    const done = (value: IssueFiltersResponse | null) => setTrackerFilters(current => ({ key: filtersKey, asked: picksText, loading: false, value,
      kept: gaveFilters(value) ? value : current?.key === filtersKey ? current.kept : null }));
    void api.filters({ expectedEpoch: epoch, projectId: project.id, service: source.service, selections: asked, include: null, withoutView: null }, { signal: controller.signal, timeoutMilliseconds: 60_000 }).then(
      reply => {
        if (controller.signal.aborted) return;
        // Values the host refuses (a text too long) are forgotten, here and for the next time: the tab asks again with those of the view.
        if (reply.status === "invalid_request" && askedKey && Object.keys(asked).length > 0) { writePicks(askedKey, {}); setPicks({ key: askedKey, values: {} }); return; }
        done(reply);
      },
      () => { if (!controller.signal.aborted) done(failedFilters); });
    return () => controller.abort();
  }, [api, epoch, available, visible, filtering, filtersKey, picksText, revision]);
  // While the filters are read again (a reload, a saved view) the tab keeps listing by those it has: the list and the item read stay.
  const filtersRead = filtering && trackerFilters?.key === filtersKey && trackerFilters.asked === picksText && trackerFilters.value !== null ? trackerFilters : null;
  const lastReply = filtering && trackerFilters?.key === filtersKey ? trackerFilters.value : null;
  // After a reply that did not give the filters, the tab still shows those it knew, so that a value chosen can be changed back.
  const filterInfo = gaveFilters(lastReply) || !lastInfo ? lastReply : lastInfo;
  // Why the filters are not all there: a file of views that does not parse, a tracker that did not give them, a request that failed.
  const filtersProblem = lastReply && (lastReply.problem ?? (lastReply.status === "ok" ? null : t("The filters could not be read.")));
  // The tab lists by what it shows: after a reply that did not give the filters, by those it still shows with the values chosen
  // in it, not by none, which the host would take as the values of the view; with none known, by the view, as the host does.
  const readFailed = filtersRead !== null && !gaveFilters(filtersRead.value);
  const listedInfo = filtersRead === null ? null : readFailed ? filterInfo : filtersRead.value;
  const readSelected = listedInfo?.status !== "ok" ? {} : readFailed ? { ...listedInfo.selected, ...sentPicks } : listedInfo.selected;
  // A filter the tab may change is passed on even without a value: the listing then lists every item, not the value of the view.
  const listed = listedFilters(readSelected, listedInfo?.status === "ok" ? listedInfo.pick : []);
  // A listing of issues waits for the filters, so that the whole project is not listed first.
  const filtersReady = !filtering || filtersRead !== null;
  const pickValue = (id: string, value: string) => {
    if (!picksKey) return;
    const next = choosePick(storedPicks, filterInfo?.status === "ok" ? filterInfo.filters : [], id, value);
    setPicks({ key: picksKey, values: next });
    writePicks(picksKey, next);
    setSelected(null);
  };
  const resetPick = (id: string) => {
    if (!picksKey) return;
    const next = forgetPick(storedPicks, filterInfo?.status === "ok" ? filterInfo.filters : [], id);
    setPicks({ key: picksKey, values: next });
    writePicks(picksKey, next);
    setSelected(null);
  };

  // The views of the tracker: the toolbar switches between them, and a window of the tab manages them.
  const currentView = filterInfo?.status === "ok" ? filterInfo.current : null;
  const views = filterInfo?.status === "ok" ? filterInfo.views : [];
  const [choosingView, setChoosingView] = useState(false);
  const selectView = async (key: string) => {
    if (!available || !project || !source || !api.selectView || choosingView) return;
    const view = views.find(candidate => viewKey(candidate) === key) ?? null;
    setChoosingView(true);
    try {
      const reply = await api.selectView({ expectedEpoch: epoch, projectId: project.id, service: source.service, name: view?.name ?? null }, { timeoutMilliseconds: 20_000 });
      if (reply.status !== "ok") throw new Error(reply.status);
      setSelected(null);
      setViewChanges(value => value + 1);
    } catch { onNotice(t("The view could not be chosen.")); }
    finally { setChoosingView(false); }
  };
  const [viewsOpen, setViewsOpen] = useState(false);
  // The same object from one render to the next: the window reads the views again, losing what is edited, when it changes.
  const viewsApi = useMemo<IssueViewsApi | null>(() => api.filters && api.fields && api.views && api.saveView && api.renameView && api.removeView
    ? { filters: api.filters, fields: api.fields, views: api.views, saveView: api.saveView, renameView: api.renameView, removeView: api.removeView } : null, [api]);
  const canManage = filtering && !!viewsApi && filterInfo?.status === "ok" && filterInfo.filters.length > 0;
  const valuesText = (shape: IssueFilterField) => splitValues(filterInfo?.selected[shape.id])
    .map(id => { const value = shape.values.find(candidate => candidate.id === id); return value ? trackerText(value.label, t) : id; }).join(", ");
  // The filters the view lists by, those the tab may change first, in its order.
  const active = shownKind !== "issue" || filterInfo?.status !== "ok" ? []
    : filterInfo.active.map(id => filterInfo.filters.find(candidate => candidate.id === id)).filter(candidate => !!candidate)
      .map(shape => ({ id: shape.id, shape, label: trackerText(shape.label, t), text: valuesText(shape), pick: filterInfo.pick.includes(shape.id) }));
  const allText = (shape: IssueFilterField) => shape.allLabel ? trackerText(shape.allLabel, t) : t("All");
  const fixedFilters = active.filter(item => !item.pick);
  // A filter the tab may change shows the value chosen in it while there is one, not the host's last reply: two quick clicks keep both.
  const pickedValue = (id: string) => id in sentPicks ? sentPicks[id] : filterInfo?.selected[id] ?? "";
  const pickedText = (shape: IssueFilterField) => {
    const value = shape.values.find(candidate => candidate.id === pickedValue(shape.id));
    return value ? filterValueText({ label: trackerText(value.label, t), detail: value.detail && trackerText(value.detail, t) }) : allText(shape);
  };
  const pickControl = (shape: IssueFilterField) => shape.kind === "text"
    ? <TextFilter label={trackerText(shape.label, t)} allText={allText(shape)} value={pickedValue(shape.id)} onChange={value => pickValue(shape.id, value)} />
    : shape.multiple
    ? <MultiChoice label={trackerText(shape.label, t)} bare values={shape.values} chosen={splitValues(pickedValue(shape.id))} allText={allText(shape)}
        text={value => trackerText(value.label, t)} onChange={values => pickValue(shape.id, joinValues(values))} />
    : <PickChoice label={trackerText(shape.label, t)} title={shape.problem ?? pickedText(shape)} chosen={pickedValue(shape.id)} allText={allText(shape)}
        values={shape.values.map(value => ({ id: value.id, label: trackerText(value.label, t), detail: value.detail && trackerText(value.detail, t) }))}
        onChange={value => pickValue(shape.id, value)} />;
  const viewControls = shownKind === "issue" && filterInfo?.status === "ok" && (views.length > 0 || canManage) && <div className="issues-views" role="group" aria-label={t("View")}>
    {views.length > 0 && api.selectView && <HTMLSelect className="issues-view" aria-label={t("View")} disabled={choosingView}
      value={currentView ? viewKey(currentView) : ""} onChange={event => void selectView(event.target.value)}>
      {!currentView && <option value="">{t("No view")}</option>}
      {viewSources.map(group => views.some(candidate => candidate.source === group) && <optgroup key={group} label={t(viewSourceLabel(group))}>
        {views.filter(candidate => candidate.source === group).map(candidate => <option key={viewKey(candidate)} value={viewKey(candidate)}>{candidate.name}</option>)}
      </optgroup>)}
    </HTMLSelect>}
    {canManage && <Button size="small" variant="minimal" icon={<AppIcon name="settings" size={15} />} className="issues-views-manage" aria-label={t("Manage views…")}
      title={t("Manage views…")} text={views.length === 0 ? t("Manage views…") : undefined} onClick={() => setViewsOpen(true)} />}
  </div>;

  const wanted = useMemo<IssueListKey | null>(() => project && source && filtersReady
    ? { projectId: project.id, service: source.service, kind: shownKind, filter: shownFilter, query, part: listedPart, filters: shownKind === "issue" ? listed.text : "" } : null,
    [project?.id, source?.service, shownKind, shownFilter, query, listedPart, filtersReady, listed.text]);

  useEffect(() => {
    if (!available || !visible || !wanted) return;
    const controller = new AbortController();
    setListing(current => sameList(current?.key ?? null, wanted) ? { ...current!, loading: true } : { key: wanted, loading: true, items: [], more: false, problem: null, needsSignIn: false });
    void api.list({ expectedEpoch: epoch, projectId: wanted.projectId, service: wanted.service, kind: wanted.kind, filter: wanted.filter, query: wanted.query || null, limit: 50, part: wanted.part,
      filters: wanted.kind === "issue" && listed.text ? listed.values : null },
      { signal: controller.signal, timeoutMilliseconds: 60_000 }).then(
      reply => { if (!controller.signal.aborted) setListing({ key: wanted, loading: false, items: reply.status === "ok" ? reply.items : [], more: reply.more,
        problem: reply.status === "ok" ? reply.problem : t("The list could not be read."), needsSignIn: reply.needsSignIn }); },
      () => { if (!controller.signal.aborted) setListing({ key: wanted, loading: false, items: [], more: false, problem: t("The list could not be read."), needsSignIn: false }); });
    return () => controller.abort();
  }, [api, epoch, available, visible, wanted, revision]);

  const shown = sameList(listing?.key ?? null, wanted) ? listing : null;
  const items = shown?.items ?? [];
  const current = items.find(item => issueKey(item) === selected) ?? items[0] ?? null;
  const currentKey = current && wanted ? `${wanted.projectId}\n${wanted.service}\n${issueKey(current)}` : null;
  useEffect(() => {
    if (!available || !visible || !current || !wanted || !currentKey) return;
    const controller = new AbortController();
    setDetail(value => value?.key === currentKey ? value : { key: currentKey, loading: true, value: null });
    void api.read({ expectedEpoch: epoch, projectId: wanted.projectId, service: wanted.service, kind: current.kind, id: current.id }, { signal: controller.signal, timeoutMilliseconds: 60_000 }).then(
      reply => { if (!controller.signal.aborted) setDetail({ key: currentKey, loading: false, value: reply }); },
      () => { if (!controller.signal.aborted) setDetail({ key: currentKey, loading: false, value: null }); });
    return () => controller.abort();
  }, [api, epoch, available, visible, currentKey, revision]);

  const openLink = (address: string | null | undefined) => {
    if (!address || !available) return;
    void api.openLink({ expectedEpoch: epoch, address }).then(reply => { if (reply.status !== "ok") onNotice(t("The page could not be opened.")); }, () => onNotice(t("The page could not be opened.")));
  };
  // What the user looks at when a start ends: its session is opened only when it is still that item.
  const shownKey = useRef<string | null>(null);
  shownKey.current = visible ? currentKey : null;
  const start = async (item: IssueRow, way: WorkStart) => {
    if (!available || !wanted) return;
    const key = `${wanted.projectId}\n${wanted.service}\n${issueKey(item)}`;
    if (startingNow.current.has(key)) return;
    startingNow.current.add(key);
    setStarting(value => new Set(value).add(key));
    try {
      const reply = await api.start({ expectedEpoch: epoch, projectId: wanted.projectId, service: wanted.service, kind: item.kind, id: item.id, worktree: way === "worktree", sessionId: null,
        ...startRequest(runSettings, runChoices.shown) }, { timeoutMilliseconds: 600_000 });
      if (reply.sessionId) {
        if (shownKey.current === key || !onStarted) { setExpanded(false); onOpenSession(reply.sessionId, wanted.projectId); }
        else onStarted(reply.sessionId, wanted.projectId, `${issueReference(item)} ${item.title}`);
      }
      if (reply.status !== "ok") { const known = startRefusal(reply.reason); onNotice(known ? t(known) : reply.message ?? t("The work did not start.")); }
    } catch { onNotice(t("The work did not start.")); }
    finally { startingNow.current.delete(key); setStarting(value => { const next = new Set(value); next.delete(key); return next; }); }
  };
  const startingKey = (item: IssueRow) => wanted ? `${wanted.projectId}\n${wanted.service}\n${issueKey(item)}` : null;

  if (!available) return <div className="work-page issues-page" onPointerDown={onActivate}><p className="work-empty">{t("Issues are unavailable in this window.")}</p></div>;
  if (!project) return <div className="work-page issues-page" onPointerDown={onActivate}><p className="work-empty">{t("No projects in this snapshot.")}</p></div>;
  const loadingSources = !sources || sources.projectId !== project?.id || sources.loading && known.length === 0;
  const noun = kindLabel(shownKind, source?.service, true);
  const command = source ? signInCommand(source.service) : null;
  const read = detail?.key === currentKey ? detail : null;
  const body = current && <IssueBody item={read?.value?.item ?? current} read={read} service={source?.service ?? null} />;
  const runsWith = runChoices.provider && <div className="work-runs-with" role="group" aria-label={t("Runs with")}>
    <span className="work-runs-with-label">{t("Runs with")}</span>
    <SessionSettingsChip id="issue" providers={providers} choices={runChoices} value={runSettings} onChange={setRunSettings} />
  </div>;
  const actions = current && <IssueActions item={current} service={source?.name ?? ""} preferredStart={preferredStart} starting={starting.has(startingKey(current) ?? "")} runsWith={runsWith}
    onStart={way => void start(current, way)}
    onOpen={() => openLink(current.url)} onCopy={() => void navigator.clipboard?.writeText(current.url).catch(() => { })} />;
  return <div className="work-page issues-page" onPointerDown={onActivate}>
    <header className="work-header">
      <span className="work-emblem issues-emblem"><AppIcon name="issueOpen" size={22} /></span>
      <div><h2>{t("Issues")}{(shown?.loading || sources?.loading) && <ActivitySpinner size={13} />}</h2><p>{t("The issues and the pull requests of your projects, from where they are kept.")}</p></div>
      {source?.url && <Button variant="minimal" icon={<AppIcon name="openExternal" size={16} />} aria-label={t("Open {service} in the browser", { service: source.name })}
        title={t("Open {service} in the browser", { service: source.name })} onClick={() => openLink(source.url)} />}
      <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} aria-label={t("Reload")} title={t("Reload")} onClick={() => setRevision(value => value + 1)} />
    </header>
    <div className="work-toolbar issues-toolbar">
      <HTMLSelect aria-label={t("Project")} value={project?.id ?? ""} onChange={event => { followed.current = false; setScope(event.target.value || null); setSelected(null); }}>
        {projects.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name}</option>)}
      </HTMLSelect>
      {known.length > 1
        ? <SegmentedControl size="small" value={source?.service ?? ""} onValueChange={value => { setService(value); setSelected(null); }}
            options={known.map(candidate => ({ value: candidate.service, label: candidate.name, icon: serviceBrand(candidate.service) ? <BrandIcon name={serviceBrand(candidate.service)!} size={14} /> : undefined }))} />
        : source && <span className="issues-source" title={source.location}>{serviceBrand(source.service) && <BrandIcon name={serviceBrand(source.service)!} size={15} />}<strong>{source.name}</strong>
            {parts.length === 0 && <span>{source.location}</span>}</span>}
      {source && parts.length > 0 && <HTMLSelect className="issues-part" aria-label={t("{service} project", { service: source.name })} title={source.location} value={listedPart ?? ""}
        onChange={event => { setPart(event.target.value || null); setSelected(null); }}>
        <option value="">{t(allPartsLabel(source.service))}</option>
        {parts.map(candidate => <option key={candidate} value={candidate}>{candidate}</option>)}
      </HTMLSelect>}
      {viewControls}
      {kinds.length > 1 && <SegmentedControl size="small" value={shownKind} onValueChange={value => { setKind(value as IssueKind); setSelected(null); }}
        options={kinds.map(candidate => ({ value: candidate, label: t(kindLabel(candidate, source?.service, true)) }))} />}
      <InputGroup size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={typed} disabled={!source}
        placeholder={t("Filter by words or number")} aria-label={t("Filter by words or number")} onChange={event => setTyped(event.target.value)} />
    </div>
    {/* What the view lists by, at a glance: first the filters it lets the tab change, each with its values; a click on the others manages the views. */}
    {active.length > 0 && <div className="issues-active-filters">
      {active.filter(item => item.pick).map(item => <span key={item.id} className="issues-active-filter issues-pick" data-changed={item.id in sentPicks || undefined}>
        <strong>{item.label}:</strong>{pickControl(item.shape)}
        {item.id in sentPicks && <Button variant="minimal" size="small" icon={<AppIcon name="reset" size={13} />} className="issues-pick-reset"
          aria-label={t("Back to the value of the view: {filter}", { filter: item.label })} title={t("Back to the value of the view")} onClick={() => resetPick(item.id)} />}
      </span>)}
      {/* The other filters of the view are counted, and named on hover: they say why an item is not listed, without filling the line. */}
      {fixedFilters.length > 0 && <PopoverNext interactionKind="hover-target" placement="bottom-start" hoverOpenDelay={150} hoverCloseDelay={80}
        content={<div className="issues-fixed-list">
          <div role="list" aria-label={t("Filters of the view")}>
            {fixedFilters.map(item => <div key={item.id} role="listitem" className="issues-active-filter"><strong>{item.label}:</strong> {item.text}</div>)}
          </div>
          {canManage && <small>{t("Click to manage the views.")}</small>}
        </div>}>
        <button type="button" className="issues-active-fixed" disabled={!canManage} onClick={() => setViewsOpen(true)}
          aria-label={[fixedFilters.length === 1 ? t("1 more filter") : t("{count} more filters", { count: fixedFilters.length }), ...fixedFilters.map(item => `${item.label}: ${item.text}`)].join(". ")}>
          {fixedFilters.length === 1 ? t(active.some(item => item.pick) ? "+ 1 filter" : "1 filter")
            : t(active.some(item => item.pick) ? "+ {count} filters" : "{count} filters", { count: fixedFilters.length })}
        </button>
      </PopoverNext>}
    </div>}
    {loadingSources ? <p className="work-empty"><ActivitySpinner size={16} /></p>
      : !source ? <section className="work-hero">
          <h3>{t("No tracker for this project")}</h3>
          <p>{t("The folder of this project has no remote on GitHub, GitLab, Azure DevOps or Bitbucket, and no plugin gives it a tracker.")}</p>
        </section>
      : <>
        <Tabs id="issue-filters" className="work-stages" selectedTabId={shownFilter} onChange={next => { setFilter(next as IssueFilter); setSelected(null); }}>
          {filters.map(candidate => <Tab key={candidate} id={candidate} disabled={false} title={<>{t(issueFilterLabel(candidate))}
            {candidate === shownFilter && shown && !shown.loading && <span className="work-count" data-zero={items.length === 0 || undefined}>{items.length}{shown.more ? "+" : ""}</span>}</>} />)}
        </Tabs>
        {/* A file of views that does not parse leaves every item listed, and a tracker that did not give its filters leaves those the tab knew: the user is told why. */}
        {shownKind === "issue" && filtersProblem && <Callout intent="warning" compact className="work-notice issues-notice issues-filters-problem">{filtersProblem}</Callout>}
        {/* A list that came with a problem is incomplete, not missing: a Jira project the account does not see. */}
        {shown?.problem && <Callout intent={shown.needsSignIn || items.length > 0 ? "warning" : "danger"} compact role="alert" className="work-notice issues-notice">
          {shown.problem}{shown.needsSignIn && command && <> {t("Run {command} in a terminal, then reload.", { command })}</>}</Callout>}
        <div className="work-body">
          <div className="work-list" role="listbox" aria-label={t(noun)}>
            {!shown || shown.loading && items.length === 0 ? <p className="work-empty"><ActivitySpinner size={16} /></p>
              : items.length === 0 && !shown.problem ? <p className="work-empty">{t(query ? "Nothing matches." : "Nothing in this list.")}</p> : null}
            {items.map(item => {
              const key = issueKey(item);
              const on = current !== null && issueKey(current) === key;
              const updated = sessionTime(item.updatedAt ?? item.createdAt, locale);
              return <button type="button" key={key} role="option" aria-selected={on} className="work-row issue-row-item" data-selected={on || undefined} data-tone={issueTone(item)}
                onClick={() => setSelected(key)} onDoubleClick={() => { setSelected(key); setExpanded(true); }}>
                <span className="work-row-glyph issue-glyph"><AppIcon name={issueIcon(item)} size={16} /></span>
                <span className="work-row-text"><strong>{item.title}</strong>
                  <small className="issue-row-line"><span className="issue-row-facts"><span className="issue-ref">{issueReference(item)}</span>{item.type && <> · {item.type}</>}{item.author && <> · {item.author}</>}
                    {updated.label && <> · <time dateTime={updated.dateTime} title={updated.title}>{updated.label}</time></>}</span>
                    {item.labels.slice(0, 3).map(label => <IssueLabelChip key={label.name} name={label.name} color={label.color} />)}
                    {item.labels.length > 3 && <span className="issue-more">+{item.labels.length - 3}</span>}</small></span>
                <span className="work-row-meta">
                  {starting.has(startingKey(item) ?? "") && <span className="work-row-starting"><ActivitySpinner size={11} />{t("Starting…")}</span>}
                  {!!item.comments && <span className="issue-comments" title={t("{count} comments", { count: item.comments })}><AppIcon name="chat" size={12} />{item.comments}</span>}
                  {(item.stateText || item.state !== shownFilter) && <Tag minimal round className="issue-state" data-tone={issueTone(item)}>{item.stateText ?? t(issueStateLabel(item))}</Tag>}
                </span>
              </button>;
            })}
            {shown?.more && <p className="issues-more">{t("More match. Type words to narrow the list.")}</p>}
          </div>
          <aside className="work-detail">
            {current ? <section className="work-detail-card issue-detail" aria-label={current.title}>
                <IssueHeading item={read?.value?.item ?? current} service={source.service} busy={!!read?.loading}
                  action={<Button variant="minimal" size="small" icon={<AppIcon name="expand" size={14} />} aria-label={t("Open in a window")} title={t("Open in a window")} onClick={() => setExpanded(true)} />} />
                <div className="work-detail-actions">{actions}</div>
                <div className="work-detail-text">{body}</div>
              </section>
              : <p className="work-empty">{t("Choose an item to read it.")}</p>}
          </aside>
        </div>
      </>}
    {/* The window stays while the tab reads its filters again after a change made in it, even when that read fails. */}
    {viewsOpen && viewsApi && available && project && source && <IssueViewsWindow api={viewsApi} epoch={epoch} project={project} source={source} onClose={() => setViewsOpen(false)}
      onChanged={views => views ? setViewChanges(value => value + 1) : setRevision(value => value + 1)} onOpenedFile={() => setViewsOpen(false)} />}
    {expanded && current && source && <AppWindow storageKey="codealta.desktop.issue-window.v1" title={`${issueReference(current)} ${current.title}`} titleId={windowTitle} className="work-item-window issue-window"
      preferredSize={viewport => ({ width: Math.min(980, viewport.width - 80), height: Math.min(820, viewport.height - 60) })}
      onClose={() => setExpanded(false)} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); setExpanded(false); }}>
      <div className="work-window issue-window-body">
        <IssueHeading item={read?.value?.item ?? current} service={source.service} busy={!!read?.loading} />
        <div className="work-window-text">{body}</div>
        <footer className="work-window-actions">{actions}</footer>
      </div>
    </AppWindow>}
  </div>;
}

function IssueLabelChip({ name, color }: { name: string; color: string | null }) {
  const colors = labelColors(color);
  return <span className="issue-label" style={colors ?? undefined} data-plain={!colors || undefined} title={name}>{name}</span>;
}

/** What an item is: its kind and state, its title, who opened it and when, its branches, people and labels. */
function IssueHeading({ item, service, busy, action }: { item: IssueRow; service: string; busy: boolean; action?: React.ReactNode }) {
  const { t, locale } = useShellLanguage();
  const created = sessionTime(item.createdAt, locale), updated = sessionTime(item.updatedAt, locale);
  return <header className="issue-heading">
    <div className="work-detail-kind"><AppIcon name={issueIcon(item)} size={14} />{t(kindLabel(item.kind as IssueKind, service, false))}
      <Tag minimal round className="issue-state" data-tone={issueTone(item)}>{item.stateText ?? t(issueStateLabel(item))}</Tag>
      {item.type && <Tag minimal round>{item.type}</Tag>}{item.priority && <Tag minimal round>{item.priority}</Tag>}
      {busy && <ActivitySpinner size={13} />}<span className="work-detail-spacer" />{action}</div>
    <h3><span className="issue-ref">{issueReference(item)}</span> {item.title}</h3>
    <p className="issue-facts">
      {item.author && <span><AppIcon name="user" size={12} />{item.author}</span>}
      {created.label && <span title={created.title}>{t("opened {when}", { when: created.label })}</span>}
      {updated.label && item.updatedAt !== item.createdAt && <span title={updated.title}>{t("updated {when}", { when: updated.label })}</span>}
      {item.comments !== null && item.comments > 0 && <span><AppIcon name="chat" size={12} />{item.comments}</span>}
    </p>
    {item.sourceBranch && <p className="issue-branches"><AppIcon name="branch" size={12} /><code>{item.sourceBranch}</code><AppIcon name="chevronRight" size={12} /><code>{item.targetBranch ?? "?"}</code></p>}
    {(item.assignees.length > 0 || item.labels.length > 0) && <p className="issue-tags">
      {item.assignees.map(name => <Tag key={`a:${name}`} minimal round icon={<AppIcon name="user" size={11} />} title={t(item.kind === "pull_request" ? "Reviewer or assignee" : "Assignee")}>{name}</Tag>)}
      {item.labels.map(label => <IssueLabelChip key={`l:${label.name}`} name={label.name} color={label.color} />)}</p>}
  </header>;
}

function IssueActions({ item, service, preferredStart, starting, runsWith, onStart, onOpen, onCopy }: {
  item: IssueRow; service: string; preferredStart: string; starting: boolean;
  /** The Agent chip of the session started on the item. */
  runsWith?: ReactNode;
  onStart: (way: WorkStart) => void; onOpen: () => void; onCopy: () => void;
}) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => { if (!copied) return; const timer = setTimeout(() => setCopied(false), 1500); return () => clearTimeout(timer); }, [copied]);
  const startable = item.state === "open" || item.state === "draft";
  return <>
    {startable && runsWith}
    {startable && <WorkStartButtons preferred={preferredStart === "session" ? "session" : "worktree"} here={false} disabled={starting} onStart={onStart} />}
    <div className="work-detail-buttons">
      <Button size="small" icon={<AppIcon name="openExternal" size={14} />} onClick={onOpen}>{t("Open on {service}", { service })}</Button>
      <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} onClick={() => { onCopy(); setCopied(true); }}>{t(copied ? "Copied" : "Copy link")}</Button>
      {starting && <ActivitySpinner size={13} />}
    </div>
  </>;
}

/** The description of an item and its comments, as their authors wrote them. */
function IssueBody({ item, read, service }: { item: IssueRow; read: Detail | null; service: string | null }) {
  const { t, locale } = useShellLanguage();
  if (!read || read.loading) return <p className="work-text-state"><ActivitySpinner size={16} /></p>;
  const value = read.value;
  if (!value || value.status !== "ok") return <p className="work-text-state">{t(value?.status === "not_found" ? "It is no longer there." : "It could not be read.")}</p>;
  const comments = value.comments ?? [];
  const missing: MessageKey = item.kind === "pull_request" ? "No description was written." : "No description was written.";
  return <div className="work-text issue-text" data-service={service ?? undefined}>
    {value.body?.trim() ? <MarkdownContent source={value.body} document /> : <p className="issue-nothing">{t(missing)}</p>}
    {value.truncated && <p className="work-text-more">{t("The description is longer than what is shown here. Open it on the web to read the rest.")}</p>}
    {comments.length > 0 && <section className="issue-comments-list" aria-label={t("Comments")}>
      <h4>{t("Comments")}<span className="work-count">{comments.length}{value.moreComments ? "+" : ""}</span></h4>
      {comments.map((comment, index) => {
        const when = sessionTime(comment.createdAt, locale);
        return <article key={index} className="issue-comment">
          <header><AppIcon name="user" size={12} /><strong>{comment.author ?? t("Someone")}</strong>{when.label && <time dateTime={when.dateTime} title={when.title}>{when.label}</time>}</header>
          <MarkdownContent source={comment.body} document />
        </article>;
      })}
      {value.moreComments && <p className="work-text-more">{t("There are more comments. Open it on the web to read them all.")}</p>}
    </section>}
  </div>;
}

/**
 * A filter of one value the tab may change: the button names the value alone, so that the line stays short, and its menu
 * says what more each value is (the dates of a sprint), so that the right one is found.
 */
function PickChoice({ label, title, chosen, allText, values, onChange }: {
  label: string; title: string; chosen: string; allText: string;
  values: readonly Readonly<{ id: string; label: string; detail: string | null }>[];
  onChange: (value: string) => void;
}) {
  const picked = values.find(value => value.id === chosen);
  return <PopoverNext placement="bottom-start" content={<Menu className="issues-pick-menu" role="listbox" aria-label={label}>
      {[{ id: "", label: allText, detail: null }, ...values].map(value => <MenuItem key={value.id} roleStructure="listoption" selected={value.id === chosen} className="bp6-popover-dismiss"
        text={value.label} label={value.detail ?? undefined} onClick={() => onChange(value.id)} />)}
    </Menu>}>
    <Button variant="minimal" size="small" className="issues-pick-choice" aria-label={`${label}: ${picked?.label ?? allText}`} title={title} endIcon={<AppIcon name="chevronDown" size={13} />}>{picked?.label ?? allText}</Button>
  </PopoverNext>;
}

/** A filter that takes a text: it is asked when the user presses Enter or leaves the field. The host takes 128 characters at most. */
function TextFilter({ label, allText, value, onChange }: { label: string; allText: string; value: string; onChange: (value: string) => void }) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  const commit = () => { if (text.trim() !== value) onChange(text.trim()); };
  return <InputGroup size="small" className="issues-filter-text" aria-label={label} placeholder={allText} value={text} maxLength={128}
    onChange={event => setText(event.target.value)} onBlur={commit} onKeyDown={event => { if (event.key === "Enter") commit(); }} />;
}
