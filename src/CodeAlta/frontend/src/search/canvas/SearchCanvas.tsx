import { Button, HTMLSelect, InputGroup, NonIdealState, ProgressBar, Tab, Tabs } from "@blueprintjs/core";
import { useCallback, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import type { LocalSearchActivityResponse, LocalSearchHit, LocalSearchOverviewResponse, LocalSearchQueryResponse, LocalSearchStatusResponse } from "#neoastra";
import { AppIcon, type IconName } from "../../AppIcon";
import { CalendarHeatmap, Chart, StatTile } from "../../charts";
import { Block, Choice, DataTable, RankedBars, type Column, type RankedItem } from "../../statistics/blocks";
import { durationParts } from "../meaningEstimate";
import { meaningStateText } from "../SearchSettings";
import { useText } from "../../statistics/text";
import type { SearchCanvasApi } from "./api";
import { watchIndex } from "./canvasWatch";
import { activityDays, activityOption, emptyPercent, browseLimit, embeddingSpeed, exploreView, formatSize, historyData, historyRange, hitTarget, indexState, kindCounts, kindLabels, keptOutCount, meaningTile, moveActive,
  passProgress, projectCells, projectTreemapOption, queryDelayMilliseconds, queryLimit, queryMode, rankParts, searchKinds, shownDate, snippetParts, sourceLabel, totalItems,
  wordsOnlyDays, type IndexState, type MeaningTile, type SearchKindName, type SearchMode } from "./model";

// The Search canvas: Overview (what the index holds for the space) and Explore (what a query finds in it, and why). It asks `api` for
// everything, reads again when the host says the index changed, and asks nothing while it is hidden. While search by meaning is on, it says
// how much of the space is searchable by meaning, and Explore can search by words, by meaning or both.

/** What the canvas does through the window: open what a hit leads to, run a command, tell the user. */
export type SearchCanvasActions = Readonly<{
  openSession(sessionId: string): void;
  openMessage(sessionId: string, offset: string): void;
  openFile(path: string): void;
  openDocumentation(page: string | null, anchor: string | null): void;
  /** Runs a command of the window by its name; false when it cannot run now. */
  runCommand(name: string): boolean;
  notify(message: string, tone: "info" | "warning" | "danger"): void;
}>;

export type SearchCanvasProject = Readonly<{ id: string; name: string; path: string }>;

export type SearchCanvasProps = Readonly<{
  /** The questions of the canvas, or null while the window has no host. */
  api: SearchCanvasApi | null;
  /** The projects of the space, or null while the window has not read them. */
  projects: readonly SearchCanvasProject[] | null;
  visible: boolean;
  /** The colors of the series of the charts, and the muted color. */
  colors: Readonly<{ series: readonly string[]; muted: string }>;
  actions: SearchCanvasActions;
}>;

type PageId = "overview" | "explore" | "activity";
type IndexData = Readonly<{ status: LocalSearchStatusResponse | null; overview: LocalSearchOverviewResponse | null; failed: boolean }>;
const kindIcons: Readonly<Record<SearchKindName, IconName>> = { message: "chat", session: "history", note: "notes", plan: "plan", task: "task", doc: "documentation",
  skill: "skill", tool: "tool", mcp: "server", plugin: "plugin", command: "goto" };
const noProjects: readonly SearchCanvasProject[] = Object.freeze([]);
const noIds: readonly string[] = Object.freeze([]);
const contentKinds: ReadonlySet<string> = new Set(["message", "session", "note", "plan", "task"]);

/** What the index holds and where its indexer is: read when the canvas is shown, and again each time the host says it changed. */
function useIndexData(api: SearchCanvasApi | null, visible: boolean): IndexData & { reload: () => void } {
  const [data, setData] = useState<IndexData>({ status: null, overview: null, failed: false });
  const [revision, setRevision] = useState(0);
  const reload = useCallback(() => setRevision(value => value + 1), []);
  const available = data.status === null || data.status.status === "ok";
  useEffect(() => {
    if (!api || !visible || !available) return;
    return watchIndex(signal => api.watch(signal), reload);
  }, [api, visible, available, reload]);
  useEffect(() => {
    if (!api || !visible) return;
    const abort = new AbortController();
    Promise.all([api.status(abort.signal), api.overview(abort.signal)]).then(([status, overview]) => {
      // An index that could not be read says so in its blocks; a host without search says it in place of the pages.
      if (!abort.signal.aborted) setData({ status: status.status === "read_failed" ? null : status, overview: overview.status === "ok" ? overview : null,
        failed: status.status === "read_failed" || overview.status === "read_failed" });
    }, () => { if (!abort.signal.aborted) setData(current => ({ ...current, failed: true })); });
    return () => abort.abort();
  }, [api, visible, revision]);
  return { ...data, reload };
}

/** The Search canvas of a space. Mount it once per tab; it is safe under React StrictMode. */
export function SearchCanvas({ api, projects, visible, colors, actions }: SearchCanvasProps) {
  const { t } = useText();
  const [page, setPage] = useState<PageId>("overview");
  const [explore, setExplore] = useState<ExploreState>({ text: "", kind: null, projectId: null, mode: "auto" });
  const index = useIndexData(api, visible);
  const tabsId = useId();
  const status = index.status;
  const shownProjects = projects ?? noProjects;
  const goExplore = useCallback((change: Partial<{ kind: SearchKindName | null; projectId: string | null }>) => {
    setExplore(current => ({ ...current, ...change }));
    setPage("explore");
  }, []);
  if (!api || status && status.status !== "ok") {
    return <div className="search-canvas" data-visible={visible}>
      <NonIdealState className="search-canvas-state" icon={<AppIcon name="search" size={28} />} title={t("Search is not available here.")}
        description={t("The search index belongs to the window that runs CodeAlta.")} />
    </div>;
  }
  if (status?.settings && !status.settings.enabled) {
    return <div className="search-canvas" data-visible={visible}>
      <NonIdealState className="search-canvas-state" icon={<AppIcon name="search" size={28} />} title={t("Search is turned off")}
        description={t("Turn it on in Settings › Search to index the sessions, the guide and what is installed.")} />
    </div>;
  }
  return <div className="search-canvas" data-visible={visible}>
    <Tabs id={tabsId} className="stats-tabs" selectedTabId={page} onChange={next => setPage(next as PageId)} renderActiveTabPanelOnly animate={false} aria-label={t("Search pages")}>
      <Tab id="overview" title={t("Overview")} panel={<div className="stats-page" data-page="overview">
        <OverviewPage index={index} projects={shownProjects} visible={visible} colors={colors} onKind={kind => goExplore({ kind, projectId: null })}
          onProject={projectId => goExplore({ projectId, kind: null })}
          onMeaning={() => { if (!actions.runCommand("search_meaning")) actions.notify(t("{name} is not available here.", { name: t("Search by meaning") }), "warning"); }} />
      </div>} />
      <Tab id="explore" title={t("Explore")} panel={<div className="stats-page" data-page="explore">
        <ExplorePage api={api} state={explore} onState={setExplore} overview={index.overview} projects={shownProjects} visible={visible} actions={actions}
          meaningOn={status?.semantic?.enabled === true && status.semantic.state !== "off"} />
      </div>} />
      <Tab id="activity" title={t("Activity")} panel={<div className="stats-page" data-page="activity">
        <ActivityPage api={api} visible={visible} colors={colors} revision={index.status} meaningOn={status?.semantic?.enabled === true} />
      </div>} />
    </Tabs>
  </div>;
}

/** The hatch of a project kept out of search, as ECharts fills an area with a picture: thin diagonal lines in the muted color. */
function hatchPattern(color: string): Readonly<{ image: HTMLCanvasElement; repeat: "repeat" }> | undefined {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 8;
  const context = canvas.getContext("2d");
  if (!context) return undefined;
  context.strokeStyle = color;
  context.globalAlpha = 0.55;
  context.lineWidth = 1.5;
  context.beginPath();
  context.moveTo(-2, 10); context.lineTo(10, -2);
  context.moveTo(-2, 2); context.lineTo(2, -2);
  context.moveTo(6, 10); context.lineTo(10, 6);
  context.stroke();
  return { image: canvas, repeat: "repeat" };
}

/** The tile of search by meaning: the share of the space searchable by meaning, or where its first indexing is. */
function meaningTileText(t: ReturnType<typeof useText>["t"], tile: MeaningTile, coverage: Readonly<{ chunks: number; embedded: number }> | null | undefined, number: Intl.NumberFormat)
  : Readonly<{ label: string; value: string; note?: string }> {
  const passages = coverage ? t("{embedded} of {total} passages", { embedded: number.format(coverage.embedded), total: number.format(coverage.chunks) }) : undefined;
  switch (tile.kind) {
    case "words": return { label: t("Searchable"), value: t("By words only") };
    case "share": return { label: t("By meaning"), value: `${tile.percent}%`, note: passages };
    case "downloading": return { label: t("By meaning"), value: t("Downloading"), note: tile.percent === null ? undefined : t("{percent}% of the model", { percent: tile.percent }) };
    case "loading": return { label: t("By meaning"), value: t("Loading"), note: t("Loading the model…") };
    case "paused": return { label: t("By meaning"), value: `${tile.percent}%`, note: t("Paused") };
    case "indexing": {
      const left = tile.secondsLeft === null ? null : durationParts(tile.secondsLeft);
      const remaining = !left ? null : left.unit === "seconds" ? t("about {count} s left", { count: left.value })
        : left.unit === "minutes" ? t("about {count} min left", { count: left.value }) : t("about {hours} h {minutes} min left", { hours: left.value, minutes: left.minutes });
      return { label: t("By meaning"), value: `${tile.percent}%`, note: remaining ?? t("Indexing") };
    }
    default: return { label: t("By meaning"), value: t("Stopped"), note: tile.reason || undefined };
  }
}

/** The words of the state of the index. */
function stateLabel(t: ReturnType<typeof useText>["t"], state: IndexState): string {
  switch (state) {
    case "off": return t("Turned off");
    case "starting": return t("Starting");
    case "building": return t("Building the index");
    case "updating": return t("Updating");
    default: return t("Ready");
  }
}

function OverviewPage({ index, projects, visible, colors, onKind, onProject, onMeaning }: Readonly<{
  index: IndexData & { reload: () => void }; projects: readonly SearchCanvasProject[]; visible: boolean; colors: SearchCanvasProps["colors"];
  onKind: (kind: SearchKindName) => void; onProject: (projectId: string) => void;
  /** Opens the dialog of search by meaning in the window. */
  onMeaning: () => void;
}>) {
  const { t, locale } = useText();
  const { status, overview } = index;
  const number = useMemo(() => new Intl.NumberFormat(locale), [locale]);
  const query = { loading: !overview && !index.failed, error: !overview && index.failed ? new Error(t("The index could not be read.")) : null, retry: index.reload, refreshing: false, placeholder: false };
  const excluded = status?.settings?.excludedProjects ?? noIds;
  const keptOut = keptOutCount(projects, excluded);
  const items = overview ? totalItems(overview.items) : 0;
  const range = useMemo(() => historyRange(new Date()), []);
  const days = useMemo(() => overview ? historyData(overview.days, range) : [], [overview, range]);
  const kinds = useMemo<RankedItem[]>(() => overview ? kindCounts(overview.items).map(row => ({ key: row.kind, label: t(kindLabels[row.kind]), value: row.count,
    text: number.format(row.count), share: items ? row.count / items : 0 })) : [], [overview, t, number, items]);
  const cells = useMemo(() => projectCells(projects, overview?.projects ?? {}, excluded), [projects, overview, excluded]);
  const hatch = useMemo(() => hatchPattern(colors.muted), [colors.muted]);
  const tree = useMemo(() => projectTreemapOption(cells, colors.series, colors.muted,
    { name: t("Items"), keptOut: t("kept out"), format: value => t("{count} items", { count: number.format(value) }) }, hatch), [cells, colors, t, number, hatch]);
  const state = status ? indexState(status) : null;
  const progress = status ? passProgress(status) : null;
  const semantic = status?.semantic ?? null;
  const meaningOn = semantic?.enabled === true;
  const tile = meaningTileText(t, meaningTile(semantic, overview?.meaning), overview?.meaning, number);
  // The days whose turns are not all searchable by meaning yet, hatched while search by meaning is on.
  const hatched = useMemo(() => meaningOn && overview?.meaning ? wordsOnlyDays(overview.meaning.days, range) : [], [meaningOn, overview, range]);
  const wordsOnly = useMemo(() => new Set(hatched), [hatched]);
  const speed = embeddingSpeed(semantic?.chunksPerSecondTenths);
  const time = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }), [locale]);
  const sourceColumns: Column<LocalSearchOverviewResponse["sources"][number]>[] = [
    { id: "family", header: t("Source"), wide: true, sort: row => row.family, render: row => { const label = sourceLabel(row.family); return label ? t(label) : row.family; } },
    { id: "sources", header: t("Read from"), align: "end", sort: row => row.sources, render: row => number.format(row.sources) },
    { id: "check", header: t("Last check"), align: "end", sort: row => row.lastCheck ?? "", render: row => row.lastCheck ? time.format(new Date(row.lastCheck)) : t("Not checked yet") },
    { id: "failed", header: t("Failures"), align: "end", sort: row => row.failed, render: row => row.failed
      ? <span className="search-source-failed" title={row.lastError ?? undefined}>{number.format(row.failed)}</span> : "–" },
  ];
  return <>
    <div className="stats-tiles" data-refreshing={!overview || undefined}>
      <StatTile label={t("Items")} value={overview ? number.format(items) : "–"}
        note={overview ? overview.recent ? t("+{count} this week", { count: number.format(overview.recent) }) : t("Nothing new this week") : undefined} />
      <StatTile label={t("Sessions")} value={overview ? number.format(overview.sessions) : "–"}
        note={keptOut === 0 ? t("No project kept out") : keptOut === 1 ? t("1 project kept out") : t("{count} projects kept out", { count: number.format(keptOut) })} />
      <StatTile label={tile.label} value={tile.value} note={meaningOn ? tile.note : overview ? t("{count} passages", { count: number.format(overview.chunks) }) : undefined} />
      <StatTile label={t("Size on disk")} value={overview ? formatSize(overview.kilobytes, locale) : "–"} note={t("The whole index")} />
    </div>
    <div className="stats-grid">
      <Block title={t("History")} caption={meaningOn ? t("The turns indexed each day; hatched, those searchable by words only so far") : t("The turns indexed each day, searchable by words only")}
        span={12} minHeight={96} query={query} empty={overview !== null && days.length === 0} emptyText={t("No turn is indexed yet.")}>
        <div className="stats-calendar"><CalendarHeatmap data={days} from={range.from} to={range.to} ariaLabel={t("The turns indexed per day over the last year")} hatched={hatched}
          describe={(date, value) => {
            const day = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" }).format(new Date(`${date}T00:00:00Z`));
            return wordsOnly.has(date) ? t("{date}: {count} turns, by words only", { date: day, count: number.format(value ?? 0) }) : t("{date}: {count} turns", { date: day, count: number.format(value ?? 0) });
          }} /></div>
      </Block>
      <Block title={t("By kind")} caption={t("Choose a kind to list what is indexed")} span={6} minHeight={180} query={query} empty={overview !== null && kinds.length === 0} emptyText={t("The index is empty.")}>
        <RankedBars label={t("Items by kind")} items={kinds} onSelect={item => onKind(item.key as SearchKindName)}
          selectLabel={item => t("List the {kind}: {count}", { kind: item.label, count: item.text })} />
      </Block>
      <Block title={t("By project")} caption={t("The projects of the space, sized by their items")} span={6} minHeight={180} query={query} empty={overview !== null && cells.length === 0}
        emptyText={t("No project of the space has items.")}>
        <Chart option={tree.option} table={tree.table} ariaLabel={t("Items by project")} height={240} visible={visible} group="search-projects"
          onSelect={datum => { const id = (datum.data as { id?: string } | null)?.id; if (id && !cells.find(cell => cell.id === id)?.keptOut) onProject(id); }} />
      </Block>
      <Block title={t("Index")} caption={t("Where the indexer is, and how each source was read")} span={12} minHeight={120} query={query}>
        {status && state && <div className="search-index-state" data-state={state}>
          <div className="search-index-line">
            <span className="search-index-badge" data-state={state}>{stateLabel(t, state)}</span>
            {status.busy && progress !== null && <span className="search-index-count">{t("{done} of {total} sessions read", { done: number.format(status.journalsDone), total: number.format(status.journalsTotal) })}</span>}
            <span className="search-index-pass">{status.lastPass ? t("Last pass {time}", { time: time.format(new Date(status.lastPass)) }) : t("No pass yet")}</span>
          </div>
          {status.busy && progress !== null && <ProgressBar className="search-index-progress" value={progress} animate={false} stripes={false} intent="primary" />}
          {status.lastError && <p className="search-index-error" role="status"><AppIcon name="warning" size={14} /> {t("Last error: {message}", { message: status.lastError })}</p>}
        </div>}
        {semantic?.supported && !meaningOn && <div className="search-index-line search-meaning-offer">
          <span className="search-index-badge" data-state="off">{t("By words only")}</span>
          <span className="search-index-count">{t("Search by meaning finds what a query is about, not only its words. The model runs on this computer.")}</span>
          <Button size="small" variant="outlined" icon={<AppIcon name="search" size={14} />} onClick={() => onMeaning()}>{t("Search by meaning…")}</Button>
        </div>}
        {semantic?.supported && meaningOn && <div className="search-index-state search-meaning-state" data-state={semantic.state}>
          <div className="search-index-line">
            <span className="search-index-badge" data-state={semantic.state === "ready" ? "ready" : semantic.state === "failed" ? "failed" : semantic.state === "paused" ? "starting" : "building"}>{t("By meaning")}</span>
            <span className="search-index-count">{meaningStateText(semantic, t, locale)}</span>
            {speed !== null && semantic.state === "indexing" && <span className="search-index-pass">{t("{speed} passages a second", { speed: speed.toLocaleString(locale, { maximumFractionDigits: 1 }) })}</span>}
          </div>
          {(semantic.state === "indexing" || semantic.state === "paused") && semantic.total > 0 && <ProgressBar className="search-index-progress" value={semantic.embedded / semantic.total} animate={false} stripes={false}
            intent={semantic.state === "paused" ? "none" : "primary"} />}
        </div>}
        {overview && overview.sources.length > 0 && <DataTable label={t("Sources")} columns={sourceColumns} rows={overview.sources} rowKey={row => row.family} limit={20} />}
      </Block>
    </div>
  </>;
}

type ExploreState = Readonly<{ text: string; kind: SearchKindName | null; projectId: string | null; mode: SearchMode }>;
type ExploreResult = Readonly<{ key: string; response: LocalSearchQueryResponse | null; failed: boolean }>;

function ExplorePage({ api, state, onState, overview, projects, visible, actions, meaningOn }: Readonly<{
  api: SearchCanvasApi; state: ExploreState; onState: (change: (current: ExploreState) => ExploreState) => void; overview: LocalSearchOverviewResponse | null;
  projects: readonly SearchCanvasProject[]; visible: boolean; actions: SearchCanvasActions;
  /** Whether search by meaning is on: Explore then offers words, meaning or both. */
  meaningOn: boolean;
}>) {
  const { t, locale } = useText();
  const number = useMemo(() => new Intl.NumberFormat(locale), [locale]);
  const view = exploreView(state.text, state.kind);
  const mode = queryMode(state.mode, meaningOn);
  const key = JSON.stringify([view, state.text.trim(), state.kind, state.projectId, view === "query" ? mode : null]);
  const [result, setResult] = useState<ExploreResult | null>(null);
  const [active, setActive] = useState(-1);
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLOListElement>(null);
  useEffect(() => {
    if (!visible || view === "kinds") return;
    const abort = new AbortController();
    // The query waits for the last key; a listing of a kind is asked at once.
    const timer = setTimeout(() => {
      const call = view === "query"
        ? api.query({ query: state.text.trim(), kind: state.kind, projectId: state.projectId, typing: true, limit: queryLimit, mode }, abort.signal)
        : api.browse(state.kind!, state.projectId, browseLimit, abort.signal);
      call.then(response => { if (!abort.signal.aborted) { setResult({ key, response, failed: false }); setActive(-1); } },
        () => { if (!abort.signal.aborted) setResult({ key, response: null, failed: true }); });
    }, view === "query" ? queryDelayMilliseconds : 0);
    return () => { clearTimeout(timer); abort.abort(); };
  }, [api, visible, view, key]); // eslint-disable-line react-hooks/exhaustive-deps
  const hits = view !== "kinds" && result?.response?.status === "ok" ? result.response.hits : [];
  const stale = result?.key !== key;
  useEffect(() => { if (active >= 0) list.current?.querySelector<HTMLElement>(`[data-index="${active}"]`)?.scrollIntoView({ block: "nearest" }); }, [active]);
  const projectName = useCallback((id: string | null) => id ? projects.find(project => project.id.toLowerCase() === id.toLowerCase())?.name ?? null : null, [projects]);

  async function open(hit: LocalSearchHit) {
    const target = hitTarget(hit, projects);
    if (!target) return;
    switch (target.kind) {
      case "message": {
        let located;
        try { located = await api.locate(target.ref, new AbortController().signal); } catch { actions.notify(t("This could not be opened."), "danger"); return; }
        if (located.status === "ok" && located.sessionId && located.offset) actions.openMessage(located.sessionId, located.offset);
        else if (located.status === "gone" && located.sessionId) { actions.notify(t("The message is no longer in its session."), "warning"); actions.openSession(located.sessionId); }
        else if (located.status === "session_missing" || located.status === "not_found") actions.notify(t("The session of this message no longer exists."), "warning");
        else actions.notify(t("This could not be opened."), "danger");
        return;
      }
      case "session": actions.openSession(target.sessionId); return;
      case "file": actions.openFile(target.path); return;
      case "documentation": actions.openDocumentation(target.page, target.anchor); return;
      case "command": if (!actions.runCommand(target.id)) actions.notify(t("{name} is not available here.", { name: hit.title }), "warning"); return;
    }
  }

  function onKey(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      setActive(current => moveActive(current, hits.length, event.key === "ArrowDown" ? 1 : -1));
    } else if (event.key === "Enter" && hits.length > 0) {
      event.preventDefault();
      void open(hits[Math.max(0, active)]);
    } else if (event.key === "Escape" && state.text) {
      event.preventDefault();
      onState(current => ({ ...current, text: "" }));
    }
  }

  const time = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }), [locale]);
  const counts = overview ? kindCounts(overview.items) : [];
  const response = result?.response;
  return <div className="search-explore">
    <div className="search-explore-bar">
      <InputGroup inputRef={input} className="search-explore-input" leftElement={<span className="search-explore-icon"><AppIcon name="search" size={15} /></span>} value={state.text} autoFocus
        placeholder={t("Search the prompts, the answers, the guide, the skills and the tools")} aria-label={t("Search query")} aria-controls="search-explore-hits"
        aria-activedescendant={active >= 0 ? `search-hit-${active}` : undefined}
        onChange={event => { const text = event.currentTarget.value; onState(current => ({ ...current, text })); }} onKeyDown={onKey}
        rightElement={state.text ? <Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Clear")} onClick={() => { onState(current => ({ ...current, text: "" })); input.current?.focus(); }} /> : undefined} />
      <HTMLSelect className="search-explore-project" value={state.projectId ?? ""} aria-label={t("Project")} onChange={event => { const projectId = event.currentTarget.value || null; onState(current => ({ ...current, projectId })); }}
        options={[{ value: "", label: t("All projects") }, ...projects.map(project => ({ value: project.id, label: project.name }))]} />
      {meaningOn && <Choice<SearchMode> label={t("Search mode")} value={state.mode} onChange={next => onState(current => ({ ...current, mode: next }))}
        options={[{ value: "words", label: t("Words") }, { value: "meaning", label: t("Meaning") }, { value: "auto", label: t("Both") }]} />}
    </div>
    <div className="search-kinds" role="group" aria-label={t("Kind")}>
      <button type="button" className="stats-chip search-kind-chip" aria-pressed={state.kind === null} onClick={() => onState(current => ({ ...current, kind: null }))}>{t("All")}</button>
      {searchKinds.map(kind => <button key={kind} type="button" className="stats-chip search-kind-chip" data-kind={kind} aria-pressed={state.kind === kind}
        onClick={() => onState(current => ({ ...current, kind: current.kind === kind ? null : kind }))}>
        <AppIcon name={kindIcons[kind]} size={13} />{t(kindLabels[kind])}</button>)}
    </div>
    {view === "kinds" ? <section className="search-browse-kinds" aria-label={t("What is indexed")}>
      <p className="search-explore-hint">{t("Type to search by words, or choose a kind to list what is indexed.")}</p>
      {counts.length > 0 ? <ul className="stats-records">
        {counts.map(row => <li key={row.kind}><button type="button" className="stats-record" onClick={() => onState(current => ({ ...current, kind: row.kind }))}>
          <span className="stats-record-label"><AppIcon name={kindIcons[row.kind]} size={13} /> {t(kindLabels[row.kind])}</span>
          <strong>{number.format(row.count)}</strong>
        </button></li>)}
      </ul> : <p className="stats-empty">{overview ? t("The index is empty.") : t("Reading the index…")}</p>}
    </section> : <section className="search-results" aria-busy={stale || undefined} data-stale={stale || undefined}>
      <div className="search-results-head" role="status">
        {result?.failed ? <span className="search-results-error">{t("The search could not be done.")}</span>
          : response && response.status !== "ok" ? <span className="search-results-error">{t("The search could not be done.")}</span>
          : response ? <span>{view === "query"
            ? (hits.length === 1 ? t("1 hit") : t("{count} hits", { count: number.format(hits.length) })) + (response.truncated ? "+" : "") + ` · ${t("{milliseconds} ms", { milliseconds: number.format(response.milliseconds) })}`
            : (hits.length === 1 ? t("1 item") : t("{count} items", { count: number.format(hits.length) })) + (response.truncated ? "+" : "")}</span>
          : <span>{t("Searching…")}</span>}
        {view === "query" && <span className="search-results-mode">{mode === "words" ? t(meaningOn ? "By words" : "By words only") : mode === "meaning" ? t("By meaning") : t("By words and meaning")}</span>}
      </div>
      {response && hits.length === 0 && !stale && <p className="stats-empty">{view === "query" ? t("Nothing found for these words.") : t("Nothing of this kind is indexed here.")}</p>}
      <ol className="search-hits" id="search-explore-hits" ref={list} role="listbox" aria-label={t("Hits")}>
        {hits.map((hit, position) => {
          const target = hitTarget(hit, projects);
          // What is installed is dated by its file, which says nothing: only what was written shows its date.
          const date = contentKinds.has(hit.kind) ? shownDate(hit.at) : null;
          const project = projectName(hit.projectId);
          const kind = hit.kind as SearchKindName;
          return <li key={hit.ref} id={`search-hit-${position}`} role="option" aria-selected={position === active} data-index={position}>
            <button type="button" className="search-hit" data-active={position === active || undefined} data-kind={hit.kind} disabled={!target} tabIndex={-1}
              onMouseMove={() => { if (active !== position) setActive(position); }} onClick={() => void open(hit)}>
              <span className="search-hit-icon" aria-hidden="true">{kindIcons[kind] ? <AppIcon name={kindIcons[kind]} size={15} /> : null}</span>
              <span className="search-hit-main">
                <span className="search-hit-title">{hit.title}</span>
                <span className="search-hit-meta">
                  {kindLabels[kind] ? t(kindLabels[kind]) : hit.kind}
                  {hit.role === "assistant" ? ` · ${t("Answer")}` : hit.role === "user" ? ` · ${t("Prompt")}` : ""}
                  {project ? ` · ${project}` : ""}{date ? ` · ${time.format(date)}` : ""}
                </span>
                {hit.snippet && <span className="search-hit-snippet">{snippetParts(hit.snippet, hit.marks).map((part, at) => part.mark ? <mark key={at}>{part.text}</mark> : <span key={at}>{part.text}</span>)}</span>}
              </span>
              {view === "query" && <span className="search-hit-rank" title={t("The rank of the hit, and what it comes from")}>
                <span className="search-hit-position">#{hit.rank}</span>
                {rankParts(hit).map(part => <span key={part.key} className="search-hit-part" data-part={part.key} data-approximate={part.approximate || undefined}
                  title={part.approximate ? t("Found by meaning: none of the words are those of the query") : undefined}>
                  {part.key === "words" ? t("words #{rank}", { rank: part.rank }) : `${part.approximate ? "≈ " : ""}${t("meaning #{rank}", { rank: part.rank })}`}</span>)}
              </span>}
            </button>
          </li>;
        })}
      </ol>
    </section>}
  </div>;
}

/** The Activity page: how search was used in the last 90 days, by the window, by agents and by tool search. */
function ActivityPage({ api, visible, colors, revision, meaningOn }: Readonly<{
  api: SearchCanvasApi; visible: boolean; colors: SearchCanvasProps["colors"];
  /** Changes when the index was read again: the activity is read again with it. */
  revision: unknown;
  meaningOn: boolean;
}>) {
  const { t, locale } = useText();
  const number = useMemo(() => new Intl.NumberFormat(locale), [locale]);
  const [state, setState] = useState<Readonly<{ data: LocalSearchActivityResponse | null; failed: boolean; attempt: number }>>({ data: null, failed: false, attempt: 0 });
  const attempt = state.attempt;
  useEffect(() => {
    if (!visible) return;
    const abort = new AbortController();
    api.activity(abort.signal).then(data => { if (!abort.signal.aborted) setState(current => ({ ...current, data: data.status === "ok" ? data : current.data, failed: data.status !== "ok" })); },
      () => { if (!abort.signal.aborted) setState(current => ({ ...current, failed: true })); });
    return () => abort.abort();
  }, [api, visible, revision, attempt]);
  const data = state.data;
  const days = useMemo(() => activityDays(data?.days ?? [], new Date()), [data]);
  const dayLabel = useMemo(() => { const format = new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", timeZone: "UTC" }); return (day: string) => format.format(new Date(`${day}T00:00:00Z`)); }, [locale]);
  const chart = useMemo(() => activityOption(days, { window: t("Window"), agent: t("Agents"), toolSearch: t("Tool search"), other: t("Other") }, colors.series, dayLabel),
    [days, t, colors, dayLabel]);
  const query = { loading: !data && !state.failed, error: !data && state.failed ? new Error(t("The activity could not be read.")) : null,
    retry: () => setState(current => ({ ...current, attempt: current.attempt + 1 })), refreshing: false, placeholder: false };
  const today = days.at(-1);
  const todayCount = today ? today.window + today.agent + today.toolSearch + today.other : 0;
  const empty = data ? emptyPercent(data.searches, data.empty) : null;
  const milliseconds = (value: number | null | undefined) => value === null || value === undefined ? "–" : t("{milliseconds} ms", { milliseconds: number.format(value) });
  return <>
    <div className="stats-tiles" data-refreshing={!data || undefined} data-count={data && data.tokens > 0 ? 5 : 4}>
      <StatTile label={t("Searches")} value={data ? number.format(data.searches) : "–"} note={data ? t("{count} today, 90 days in all", { count: number.format(todayCount) }) : undefined} />
      <StatTile label={t("Found nothing")} value={empty === null ? "–" : `${empty}%`} note={data && data.empty > 0 ? t("{count} searches", { count: number.format(data.empty) }) : undefined} />
      <StatTile label={t("Median time by words")} value={milliseconds(data?.medianWordsMilliseconds)} />
      <StatTile label={t("Median time by meaning")} value={milliseconds(data?.medianMeaningMilliseconds)} note={meaningOn ? undefined : t("Search by meaning is off")} />
      {data && data.tokens > 0 && <StatTile label={t("Kept out of prompts")} value={t("{count} tokens", { count: new Intl.NumberFormat(locale, { notation: "compact" }).format(data.tokens) })}
        note={t("By tool search, estimated")} />}
    </div>
    <div className="stats-grid">
      <Block title={t("Searches per day")} caption={t("By who asked: the window, agents and tool search; never the words searched")} span={12} minHeight={220} query={query}
        empty={data !== null && data.searches === 0} emptyText={t("No search in the last 90 days.")}>
        <Chart option={chart.option} table={chart.table} ariaLabel={t("Searches per day, by who asked")} height={240} visible={visible} group="search-activity" />
      </Block>
    </div>
  </>;
}

