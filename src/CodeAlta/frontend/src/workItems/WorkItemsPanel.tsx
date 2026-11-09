import { useContext, useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { Alert, Button, Callout, HTMLSelect, InputGroup, SegmentedControl, Tab, Tabs, Tag } from "@blueprintjs/core";
import type { RunProvider } from "./runsWith";
import { SessionSettingsChip, useSessionSettingsChoices } from "../sessionSettings/SessionSettingsChip";
import { defaultSessionSettings, startRequest, type SessionStartRequest } from "../sessionSettings/sessionSettings";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import { reviews as reviewsApi, type ReviewsOverviewProject, type WorkspaceProject, type WorkspaceSession } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { WorkItemTags, WorkItemText, WorkStartButtons } from "./WorkItemCards";
import { TagChips, TagFilter, TagManagerDialog, TagProposalBanners, TagProposalDialog, TaskTagField, type ShownTagProposal } from "./WorkItemTagViews";
import type { WorkItemTagRow } from "#neoastra";
import { createdDay, filterWorkItems, shownTags, sortWorkItems, stageCounts, taskCategoryIcon, workDateGroup, workFilterLabel, workFilters, workItemKey, workItems,
  workItemTone, workKindIcon, workStatusLabel, workStatusTone, type WorkFilter, type WorkGrouping, type WorkItem, type WorkKind, type WorkOrder, type WorkStart } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";
import { PlanReview } from "./PlanReview";
import { reviewSessions } from "../review/reviewSessions";
import { ReviewOpenerContext, useReviewsHub } from "../review/useReviews";
import { sessionTime } from "../sessionTime";
import { checkoutName, isReviewItem, plansUnderReview, reviewCountsText, reviewItems, type ReviewWorkItem } from "./reviewItems";

/** What another part of the window asks the tab to show: the items of a project, or one item. */
export type WorkItemsFocus = Readonly<{ projectId: string | null; key?: string | null; kind?: WorkKind | "all" }>;

/**
 * The Work items tab: the follow-up tasks agents proposed and the plans, of one project or of all. What waits
 * for a decision, what is being done, what is set aside and what is closed are separate lists, so that what
 * is done does not hide what is left. An item is read on the right, and decided there: started, kept for
 * later, marked done, or removed.
 */
export function WorkItemsPanel({ hub, projects, sessions, runningSessions, projectId, visible, focus, onFocused, onActivate, onStart, onOpenSession, onOpenFile, onOpenSettings, providers = noProviders, sessionSettings = null, epoch = null }: {
  hub: WorkItemsHub;
  /** The owned host's epoch, for the review of a plan; null without one: the plan is shown without comments. */
  epoch?: string | null;
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
  /** `shown` says whether the user still looks at the item when its session exists: it is then opened, else offered. */
  onStart: (item: WorkItem, start: WorkStart, request: SessionStartRequest | null, shown: () => boolean) => Promise<boolean>;
  /** The enabled providers a new session can run with; none when the window cannot start one. */
  providers?: readonly RunProvider[];
  /** The catalogs of the form of the Agent chip of a new session of a project. */
  sessionSettings?: ((projectId: string) => SessionSettingsApi | null) | null;
  onOpenSession: (sessionId: string, projectId: string) => void;
  /** Opens the file of an item in the code editor of its project. */
  onOpenFile: (projectId: string, file: string) => void;
  onOpenSettings: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [scope, setScope] = useState<string | null>(projectId);
  const [kind, setKind] = useState<WorkKind | "all">("all");
  const [stage, setStage] = useState<WorkFilter>("todo");
  const [query, setQuery] = useState("");
  // The tags a task of the list must have one of; none chosen shows every task.
  const [tagFilter, setTagFilter] = useState<ReadonlySet<string>>(() => new Set());
  const [managingTags, setManagingTags] = useState(false);
  const [reviewing, setReviewing] = useState<ShownTagProposal | null>(null);
  // How the list is read: newest or oldest first, by day or by project; kept for the next time.
  const [view, setView] = useState(readWorkView);
  const changeView = (change: Partial<WorkView>) => setView(current => { const next = { ...current, ...change }; writeWorkView(next); return next; });
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [removing, setRemoving] = useState<WorkItem | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  // Bumped by **Reload**: the plan shown is read again with its review.
  const [reload, setReload] = useState(0);
  const reviewsHub = useReviewsHub();
  const sessionIds = useMemo(() => new Set(sessions.map(session => session.id)), [sessions]);
  const sessionById = useMemo(() => new Map(sessions.map(session => [session.id, session])), [sessions]);
  const projectById = useMemo(() => new Map(projects.map(project => [project.id, project])), [projects]);
  // The reviews in flight, beside the tasks and the plans.
  const overview = useReviewOverview(epoch, visible);
  const reviewRows = useMemo(() => reviewItems(overview, t).filter(item => projectById.has(item.projectId)), [overview, t, projectById]);
  const underReview = useMemo(() => plansUnderReview(reviewRows), [reviewRows]);
  const all = useMemo(() => [...workItems(state.projects, sessionIds), ...reviewRows].filter(item => projectById.has(item.projectId)), [state.projects, sessionIds, projectById, reviewRows]);
  // A project that has nothing is not one to choose: the list would be empty.
  const scoped = scope !== null && all.some(item => item.projectId === scope) ? scope : null;
  const counts = stageCounts(all, scoped, kind);
  const tags = useMemo(() => shownTags(state.projects, scoped), [state.projects, scoped]);
  // The tag proposals of the projects shown, each with the open tasks it still names.
  const proposals = useMemo(() => state.projects.filter(project => scoped === null || project.projectId === scoped).flatMap(project => (project.tagProposals ?? []).map(proposal => ({
    projectId: project.projectId, projectName: scoped === null ? projectById.get(project.projectId)?.name ?? null : null, proposal,
    tasks: proposal.tasks.map(match => all.find(item => item.projectId === project.projectId && item.kind === "task" && item.id === match.id)).filter((item): item is WorkItem => !!item) })))
    .filter(shown => shown.tasks.length > 0), [state.projects, scoped, projectById, all]);
  // A tag chosen for another project does not empty the list of this one.
  const chosenTags = useMemo(() => new Set([...tagFilter].filter(name => tags.some(tag => tag.name === name))), [tagFilter, tags]);
  const shown = useMemo(() => sortWorkItems(filterWorkItems(all, { projectId: scoped, kind, stage, query, tags: chosenTags }), view.order), [all, scoped, kind, stage, query, chosenTags, view.order]);
  const current = shown.find(item => workItemKey(item) === selected) ?? shown[0] ?? null;
  useEffect(() => { if (visible) void hub.refresh(); }, [hub, visible]);
  useEffect(() => {
    if (!focus || !state.loaded) return;
    setScope(focus.projectId);
    const item = focus.key ? all.find(candidate => workItemKey(candidate) === focus.key) : null;
    if (item) { setStage(item.stage); setKind("all"); setQuery(""); setTagFilter(new Set()); setSelected(focus.key!); }
    else if (focus.kind) { setKind(focus.kind); setStage("todo"); setQuery(""); setTagFilter(new Set()); }
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
  // A start says itself why it was refused. Several items start at once; each opens its session only when it is
  // still the one looked at.
  const shownKey = useRef<string | null>(null);
  shownKey.current = visible && current ? workItemKey(current) : null;
  const start = (item: WorkItem, way: WorkStart, request: SessionStartRequest | null) => void during(item, async () => {
    await onStart(item, way, request, () => shownKey.current === workItemKey(item)); return true;
  });

  if (!state.loaded) return <div className="work-page" onPointerDown={onActivate}><p className="work-empty"><ActivitySpinner size={16} /></p></div>;
  if (!state.available) return <div className="work-page" onPointerDown={onActivate}><p className="work-empty">{t("Work items are unavailable in this window.")}</p></div>;
  const withItems = projects.filter(project => all.some(item => item.projectId === project.id));
  // By project only while every project is listed: one project is one group.
  const byProject = scoped === null && view.grouping === "project";
  const groups = listGroups(shown, byProject ? withItems : null, t, locale);
  const dayFormat = new Intl.DateTimeFormat(locale, { day: "numeric", month: "short" });
  const yearFormat = new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", year: "numeric" });
  const longFormat = new Intl.DateTimeFormat(locale, { dateStyle: "full" });
  return <div className="work-page" onPointerDown={onActivate} data-empty={all.length === 0 || undefined}>
    <header className="work-header">
      <span className="work-emblem"><AppIcon name="task" size={22} /></span>
      <div><h2>{t("Work items")}{!state.complete && <ActivitySpinner size={13} />}</h2><p>{t("The follow-up tasks agents proposed, the plans and the reviews of your projects.")}</p></div>
      <Button variant="minimal" icon={<AppIcon name="settings" size={16} />} aria-label={t("Work item settings")} title={t("Work item settings")} onClick={onOpenSettings} />
      <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} aria-label={t("Reload")} title={t("Read the files again")} onClick={() => { void hub.refresh(); void reviewsHub?.refresh(); overview.refresh(); setReload(value => value + 1); }} />
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
            options={[{ value: "all", label: t("All") }, { value: "task", label: t("Tasks") }, { value: "plan", label: t("Plans") }, { value: "review", label: t("Reviews") }]} />
          <HTMLSelect aria-label={t("Project")} value={scoped ?? ""} onChange={event => setScope(event.target.value || null)}>
            <option value="">{t("All projects")}</option>
            {withItems.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}
          </HTMLSelect>
          <InputGroup size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query} placeholder={t("Search work items")}
            aria-label={t("Search work items")} onChange={event => setQuery(event.target.value)} />
          <TagFilter tags={tags} chosen={chosenTags} onChange={setTagFilter} onManage={() => setManagingTags(true)} />
          <span className="work-view" role="group" aria-label={t("Order")}>
            <Button size="small" variant="minimal" className="work-order" icon={<AppIcon name={view.order === "newest" ? "arrowDown" : "arrowUp"} size={14} />}
              title={t(view.order === "newest" ? "Oldest first" : "Newest first")} aria-pressed={view.order === "oldest"}
              onClick={() => changeView({ order: view.order === "newest" ? "oldest" : "newest" })}>{t(view.order === "newest" ? "Newest first" : "Oldest first")}</Button>
            {scoped === null && <HTMLSelect minimal className="work-grouping" aria-label={t("Group by")} value={view.grouping}
              onChange={event => changeView({ grouping: event.target.value as WorkGrouping })}
              options={[{ value: "date", label: t("Group by date") }, { value: "project", label: t("Group by project") }]} />}
          </span>
        </div>
        <TagProposalBanners proposals={proposals} onReview={setReviewing} onDismiss={shown => void hub.tag(shown.projectId, "dismiss", shown.proposal.name)} />
        <Tabs id="work-stages" className="work-stages" selectedTabId={stage} onChange={next => setStage(next as WorkFilter)}>
          {workFilters.map(filter => <Tab key={filter} id={filter} disabled={false} title={<>{t(workFilterLabel(filter))}<span className="work-count" data-zero={counts[filter] === 0 || undefined}>{counts[filter]}</span></>} />)}
        </Tabs>
        <div className="work-body">
          <div className="work-list" role="listbox" aria-label={t(workFilterLabel(stage))}>
            {shown.length === 0 && <p className="work-empty">{t(query.trim() || chosenTags.size ? "No work item matches." : stage === "todo" ? "Nothing waits for a decision." : stage === "running" ? "Nothing is in progress."
              : stage === "later" ? "Nothing is set aside for later." : "Nothing here. The settings say whether closed tasks and completed plans are kept.")}</p>}
            {groups.map(group => <section key={group.key} className="work-group" data-group={group.key}>
              {<h3><AppIcon name={byProject ? "folder" : "calendar"} size={13} />{group.label}<span className="work-count">{group.items.length}</span></h3>}
              {group.items.map(item => {
                const key = workItemKey(item);
                const runner = item.runner ? sessionById.get(item.runner) : undefined;
                const day = createdDay(item.created);
                const tone = workItemTone(item);
                return <button type="button" key={key} role="option" aria-selected={current !== null && workItemKey(current) === key} className="work-row" data-kind={item.kind}
                  data-selected={current !== null && workItemKey(current) === key || undefined} onClick={() => setSelected(key)}>
                  <span className="work-row-badge"><span className="work-row-glyph" data-tone={tone}>
                    <AppIcon name={item.kind === "task" ? taskCategoryIcon(item.category) : workKindIcon(item.kind)} size={16} /></span>
                    {item.shortId && <code className="work-row-id" data-tone={tone}>{item.shortId}</code>}</span>
                  <span className="work-row-text"><strong>{item.title}</strong>
                    <span className="work-row-sub"><TagChips tags={item.tags} /><small>{item.summary ?? item.statusText ?? item.file}</small></span></span>
                  <span className="work-row-meta">
                    {busy.has(key) && <span className="work-row-starting"><ActivitySpinner size={11} /></span>}
                    {scoped === null && !byProject && <span className="work-row-project" title={projectById.get(item.projectId)?.name}>{projectById.get(item.projectId)?.name}</span>}
                    {day && <time className="work-row-date" dateTime={item.created ?? undefined} title={t("Created {date}", { date: longFormat.format(day) })}>
                      {(day.getFullYear() === new Date().getFullYear() ? dayFormat : yearFormat).format(day)}</time>}
                    {item.kind === "plan" && underReview.has(`${item.projectId}\n${item.file}`) && <span className="work-row-review" title={t("Under review")}><AppIcon name="eye" size={13} /></span>}
                    {runner && <span className="work-runner" title={runner.title}>{runningSessions.has(runner.id) ? <ActivitySpinner size={11} /> : <AppIcon name="chat" size={11} />}<span>{runner.title}</span></span>}
                    <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>
                  </span>
                </button>;
              })}
            </section>)}
          </div>
          <aside className="work-detail">
            {current && isReviewItem(current) ? <ReviewItemDetail key={workItemKey(current)} item={current} epoch={epoch} projectName={projectById.get(current.projectId)?.name ?? null}
                requester={current.runner ? sessionById.get(current.runner) ?? null : null} onOpenSession={id => onOpenSession(id, current.projectId)}
                reviewSessions={reviewSessions(sessions, current.projectId, current.review.checkout, current.runner)} fallback={<p className="work-empty">{t("The file cannot be read.")}</p>}
                reload={reload} onForget={() => overview.refresh()} />
              : current ? <WorkItemDetail key={workItemKey(current)} hub={hub} item={current} busy={busy.has(workItemKey(current))} projectName={projectById.get(current.projectId)?.name ?? null}
                providers={providers} sessionSettings={sessionSettings} tags={shownTags(state.projects, current.projectId)}
                runner={current.runner ? sessionById.get(current.runner) ?? null : null} runnerWorking={!!current.runner && runningSessions.has(current.runner)}
                preferredStart={state.settings.start} onStart={(way, request) => start(current, way, request)} onAct={(action, value) => act(current, action, value)}
                onRemove={() => setRemoving(current)} onOpenSession={() => current.runner && onOpenSession(current.runner, current.projectId)}
                epoch={epoch} reload={reload} reviewSessions={reviewSessions(sessions, current.projectId, null, current.runner)} onOpenReviewSession={id => onOpenSession(id, current.projectId)}
                onOpenFile={() => onOpenFile(current.projectId, current.file)} />
              : <p className="work-empty">{t("Choose a work item to read it.")}</p>}
          </aside>
        </div>
      </>}
    {reviewing && <TagProposalDialog hub={hub} shown={reviewing} onClose={() => setReviewing(null)} />}
    {managingTags && <TagManagerDialog hub={hub} projectId={scoped} onClose={() => setManagingTags(false)}
      projects={withItems.map(project => { const known = state.projects.find(candidate => candidate.projectId === project.id);
        return { id: project.id, name: project.name, tags: known?.tags ?? [], declined: known?.declinedTags ?? [] }; })} />}
    <Alert isOpen={!!removing} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t("Remove")} canEscapeKeyCancel canOutsideClickCancel
      onCancel={() => setRemoving(null)} onConfirm={() => { const item = removing; setRemoving(null); if (item) act(item, "remove"); }}>
      <p>{removing && t(removing.kind === "plan" ? "Remove the plan \"{title}\"? Its file is deleted." : "Remove the task \"{title}\"? Its file is deleted.", { title: removing.title })}</p>
    </Alert>
  </div>;
}

const noProviders: readonly RunProvider[] = [];

/** How the list is read: its order and what groups it. */
type WorkView = Readonly<{ order: WorkOrder; grouping: WorkGrouping }>;
const workViewKey = "codealta.desktop.workItems.view.v1";
const defaultWorkView: WorkView = { order: "newest", grouping: "date" };

// The view chosen the last time, in the storage of the window; the default when it reads as nothing known.
function readWorkView(): WorkView {
  try {
    const value = JSON.parse(globalThis.localStorage?.getItem(workViewKey) ?? "null") as Partial<WorkView> | null;
    return { order: value?.order === "oldest" ? "oldest" : "newest", grouping: value?.grouping === "project" ? "project" : "date" };
  } catch {
    return defaultWorkView;
  }
}

function writeWorkView(view: WorkView) {
  try { globalThis.localStorage?.setItem(workViewKey, JSON.stringify(view)); } catch { /* A storage that is full or refused keeps the view for this window only. */ }
}

type ListGroup = Readonly<{ key: string; label: string; items: readonly WorkItem[] }>;

/**
 * The groups of the list, in its order: the items of each project, the projects as the window lists them; or, without
 * projects, the days the items were created (Today, Yesterday, This week, Last week, then each month), as they come.
 */
function listGroups(items: readonly WorkItem[], projects: readonly WorkspaceProject[] | null, t: ReturnType<typeof useShellLanguage>["t"], locale: string): ListGroup[] {
  if (projects) return projects.map(project => ({ key: project.id, label: project.name, items: items.filter(item => item.projectId === project.id) })).filter(group => group.items.length > 0);
  const now = new Date();
  const month = new Intl.DateTimeFormat(locale, { month: "long", year: "numeric" });
  const groups: { key: string; label: string; items: WorkItem[] }[] = [];
  for (const item of items) {
    const group = workDateGroup(item.created, now);
    const last = groups.at(-1);
    if (last?.key === group.key) { last.items.push(item); continue; }
    const label = group.label === null ? t("No date") : "key" in group.label ? t(group.label.key) : month.format(group.label.month);
    groups.push({ key: group.key, label, items: [item] });
  }
  return groups;
}

// The reviews of the projects, read while the tab is shown, and again when the reviews of the window change.
function useReviewOverview(epoch: string | null, visible: boolean): readonly ReviewsOverviewProject[] & { refresh: () => void } {
  const hub = useReviewsHub();
  const [projects, setProjects] = useState<readonly ReviewsOverviewProject[]>([]);
  const [generation, setGeneration] = useState(0);
  useEffect(() => hub?.subscribe(() => setGeneration(value => value + 1)), [hub]);
  useEffect(() => {
    if (!epoch || !visible) return;
    const controller = new AbortController();
    // Debounced: the hub tells of every list it reads again.
    const timer = setTimeout(() => {
      void reviewsApi.overview({ expectedEpoch: epoch, spaceId: null, includeFinished: true }, { signal: controller.signal, timeoutMilliseconds: 20_000 })
        .then(result => { if (!controller.signal.aborted && result.status === "ok") setProjects(result.projects); }).catch(() => { /* Kept as it was. */ });
    }, 250);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [epoch, visible, generation]);
  return useMemo(() => Object.assign([...projects], { refresh: () => setGeneration(value => value + 1) }), [projects]);
}

// A review of Work items: what it is about, who it waits for, how it went, and where it is done.
function ReviewItemDetail({ item, epoch, projectName, requester, onOpenSession, reviewSessions: reviewTo, fallback, reload, onForget }: {
  item: ReviewWorkItem; epoch: string | null; projectName: string | null; reload: number;
  /** The session that asked for the review, while its request waits. */
  requester: WorkspaceSession | null;
  onOpenSession: (sessionId: string) => void;
  reviewSessions: ReturnType<typeof reviewSessions>;
  fallback: ReactNode;
  /** Called once the review, or the reviews of a checkout that is gone, were deleted. */
  onForget: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const hub = useReviewsHub();
  const openReview = useContext(ReviewOpenerContext);
  const review = item.review;
  const scope = { projectId: item.projectId, checkout: review.checkout };
  const target = review.target === "file" && review.path ? { kind: "file" as const, path: review.path } : { kind: review.target === "branch" ? "branch" as const : "changes" as const };
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const where = checkoutName(review.checkout);
  const last = review.lastRound;
  const lastAt = last ? sessionTime(last.at, locale) : null;
  const verdict = last ? t(last.verdict === "approved" ? "Approved" : last.verdict === "changes_requested" ? "Changes requested" : "Comments") : null;
  // A Markdown file of the project folder is reviewed right here, like a plan.
  const inline = review.target === "file" && !review.checkout && !!review.path && /\.(md|markdown)$/i.test(review.path) && !!epoch && !review.missing && !review.fileMissing;
  // A review is deleted whatever its state: its comments, their replies and its rounds. The reviews of a worktree
  // that is gone are deleted together.
  const forget = async () => {
    if (!epoch) return;
    setBusy(true);
    try {
      if (review.missing && review.checkout) await reviewsApi.forgetCheckout({ expectedEpoch: epoch, projectId: item.projectId, checkout: review.checkout }, { timeoutMilliseconds: 15_000 });
      else if (!hub || !(await hub.deleteReview(scope, target)).ok) return;
      onForget();
    }
    catch { /* The row stays: the user can try again. */ }
    finally { setBusy(false); }
  };
  return <section className="work-detail-card" aria-label={item.title}>
    <header>
      <div className="work-detail-kind"><AppIcon name="eye" size={14} />{t("Review")}
        <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>{busy && <ActivitySpinner size={13} />}</div>
      <h3>{item.title}</h3>
      <div className="work-detail-tags">
        {projectName && <Tag minimal icon={<AppIcon name="folder" size={12} />}>{projectName}</Tag>}
        <Tag minimal icon={<AppIcon name={where ? "worktree" : "folder"} size={12} />} title={review.checkout ?? undefined}>{where ? `${t("Worktree")} ${where}` : t("Project folder")}</Tag>
        {review.path && <Tag minimal icon={<AppIcon name="fileText" size={12} />}>{review.path}</Tag>}
      </div>
      {review.missing && <Callout compact intent="warning" className="work-review-missing">{t("The folder of this worktree is gone. Its comments are kept until you delete them.")}</Callout>}
      {!review.missing && review.fileMissing && <Callout compact intent="warning" className="work-review-missing">{t("The file of this review is gone. Its comments are kept until you delete the review.")}</Callout>}
      {review.request && <button type="button" className="work-runner work-runner-link" title={t("Open the session that asked for the review")}
        onClick={() => onOpenSession(review.request!.sessionId)}><AppIcon name="ask" size={12} />
        <span>{t("Requested by {session}", { session: requester?.title ?? review.request.title ?? review.request.sessionId })}</span><AppIcon name="chevronRight" size={12} /></button>}
      <p className="work-detail-status">{[reviewCountsText(review, t), last && lastAt ? `${t("Last round")}: ${verdict} · ${lastAt.label}` : null].filter(Boolean).join(" · ")}</p>
    </header>
    <div className="work-detail-actions">
      <div className="work-detail-buttons">
        {!review.missing && !review.fileMissing && openReview && <Button size="small" intent="primary" icon={<AppIcon name="eye" size={14} />} onClick={() => openReview(scope, target)}>{t("Open review")}</Button>}
        {review.addressed > 0 && hub && !review.missing && !review.fileMissing && <Button size="small" disabled={busy} icon={<AppIcon name="check" size={14} />}
          onClick={() => { setBusy(true); void hub.resolveAddressed(scope, target).finally(() => setBusy(false)); }}>{t("Resolve addressed")}</Button>}
        <span className="work-detail-spacer" />
        <Button size="small" variant={review.missing || review.fileMissing ? "solid" : "minimal"} intent="danger" disabled={busy || !epoch} icon={<AppIcon name="trash" size={14} />}
          title={t("Delete review: its comments, their replies and its rounds")} onClick={() => setConfirming(true)}>{t("Delete review")}</Button>
      </div>
    </div>
    {inline && epoch && <div className="work-detail-text">
      <PlanReview epoch={epoch} projectId={item.projectId} path={review.path!} sessions={reviewTo} onOpenSession={onOpenSession} fallback={fallback} reload={reload} />
    </div>}
    <Alert isOpen={confirming} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t("Delete review")} canEscapeKeyCancel canOutsideClickCancel
      onCancel={() => setConfirming(false)} onConfirm={() => { setConfirming(false); void forget(); }}>
      <p>{t("Delete this review? Its comments, their replies and its rounds are deleted. An agent that waits for it is told the review was cancelled.")}</p>
    </Alert>
  </section>;
}

// The short id of a task, which agents name it by: a click copies it, and says so for a moment.
function ShortIdCopy({ shortId }: { shortId: string }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => { if (!copied) return; const timer = setTimeout(() => setCopied(false), 1400); return () => clearTimeout(timer); }, [copied]);
  return <button type="button" className="work-detail-id" data-copied={copied || undefined} title={t("Copy the short id")} aria-label={t("Copy the short id {id}", { id: shortId })}
    onClick={() => void navigator.clipboard?.writeText(shortId).then(() => setCopied(true), () => { /* The clipboard is unavailable: nothing was copied. */ })}>
    <code>{shortId}</code><AppIcon name={copied ? "check" : "copy"} size={12} /></button>;
}

function WorkItemDetail({ hub, item, busy, projectName, runner, runnerWorking, preferredStart, providers, sessionSettings, onStart, onAct, onRemove, onOpenSession, onOpenFile,
  epoch, reload, reviewSessions: reviewTo, onOpenReviewSession, tags }: {
  hub: WorkItemsHub; item: WorkItem; busy: boolean; projectName: string | null; reload: number;
  /** The tags of the project of the item. */
  tags: readonly WorkItemTagRow[];
  /** The host's epoch: a plan that is not done takes comments; null shows it without. */
  epoch: string | null; reviewSessions: ReturnType<typeof reviewSessions>; onOpenReviewSession: (sessionId: string) => void;
  runner: WorkspaceSession | null; runnerWorking: boolean; preferredStart: string;
  providers: readonly RunProvider[]; sessionSettings: ((projectId: string) => SessionSettingsApi | null) | null;
  onStart: (start: WorkStart, request: SessionStartRequest | null) => void; onAct: (action: string, value?: string) => void; onRemove: () => void; onOpenSession: () => void; onOpenFile: () => void;
}) {
  const { t } = useShellLanguage();
  const task = item.kind === "task";
  const startable = item.stage === "todo" || item.stage === "later";
  // What the new session runs with: what the item was proposed with, else the defaults, until the user changes it.
  const [settings, setSettings] = useState(defaultSessionSettings);
  const settingsApi = useMemo(() => startable ? sessionSettings?.(item.projectId) ?? null : null, [startable, sessionSettings, item.projectId]);
  const choices = useSessionSettingsChoices(settingsApi, startable ? providers : noProviders, item.runsWith, settings);
  // A plan that is not done is reviewed here, or in the editor's preview.
  const openReview = useContext(ReviewOpenerContext);
  const reviewable = !task && item.stage !== "closed" && !!epoch;
  return <section className="work-detail-card" aria-label={item.title}>
    <header>
      <div className="work-detail-kind"><AppIcon name={workKindIcon(item.kind)} size={14} />{t(task ? "Task" : "Plan")}
        <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>{busy && <ActivitySpinner size={13} />}
        {item.shortId && <ShortIdCopy shortId={item.shortId} />}</div>
      <h3>{item.title}</h3>
      <WorkItemTags item={item} projectName={projectName} />
      {task && <TaskTagField hub={hub} item={item} tags={tags} disabled={busy} />}
      {item.statusText && <p className="work-detail-status">{item.statusText}</p>}
      {runner && <button type="button" className="work-runner work-runner-link" title={t("Open the session that does it")} onClick={onOpenSession}>
        {runnerWorking ? <ActivitySpinner size={12} /> : <AppIcon name="chat" size={12} />}<span>{runner.title}</span><AppIcon name="chevronRight" size={12} /></button>}
    </header>
    <div className="work-detail-actions">
      {startable && choices.provider && <div className="work-runs-with" role="group" aria-label={t("Runs with")}>
        <span className="work-runs-with-label">{t("Runs with")}</span>
        <SessionSettingsChip id={`work-${item.kind}-${item.id}`} providers={providers} choices={choices} value={settings} disabled={busy} onChange={setSettings} />
      </div>}
      {startable && <WorkStartButtons preferred={preferredStart} here={false} disabled={busy} onStart={way => onStart(way, startRequest(settings, choices.shown))} />}
      <div className="work-detail-buttons">
        {task && item.stage === "todo" && <Button size="small" disabled={busy} icon={<AppIcon name="later" size={14} />} onClick={() => onAct("later")}>{t("Backlog")}</Button>}
        {task && (item.stage === "later" || item.stage === "closed") && <Button size="small" disabled={busy} icon={<AppIcon name="restore" size={14} />} onClick={() => onAct("reopen")}>{t("Back to Inbox")}</Button>}
        {item.stage === "running" && item.runner && <Button size="small" disabled={busy} icon={<AppIcon name="restore" size={14} />} title={t("No session does it any more.")}
          onClick={() => onAct("release")}>{t("Back to Inbox")}</Button>}
        {task && item.stage !== "closed" && <Button size="small" disabled={busy} icon={<AppIcon name="check" size={14} />} onClick={() => onAct("complete")}>{t("Mark done")}</Button>}
        {!task && item.status === "draft" && <Button size="small" disabled={busy} icon={<AppIcon name="checked" size={14} />} onClick={() => onAct("status", "approved")}>{t("Mark ready")}</Button>}
        {!task && item.stage !== "closed" && <Button size="small" disabled={busy} icon={<AppIcon name="check" size={14} />} onClick={() => onAct("status", "done")}>{t("Mark done")}</Button>}
        {!task && item.stage === "closed" && <Button size="small" disabled={busy} icon={<AppIcon name="restore" size={14} />} onClick={() => onAct("status", "approved")}>{t("Reopen")}</Button>}
        {task && (item.stage === "todo" || item.stage === "later") && <Button size="small" disabled={busy} icon={<AppIcon name="close" size={14} />} onClick={() => onAct("dismiss")}>{t("Dismiss")}</Button>}
        <span className="work-detail-spacer" />
        {reviewable && openReview && <Button size="small" variant="minimal" icon={<AppIcon name="chat" size={14} />} title={t("Review the plan in the editor's preview")}
          onClick={() => openReview({ projectId: item.projectId, checkout: null }, { kind: "file", path: item.file })}>{t("Review")}</Button>}
        <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} title={item.file} onClick={onOpenFile}>{t("Open file")}</Button>
        <Button size="small" variant="minimal" intent="danger" disabled={busy} icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove")} title={t("Remove")} onClick={onRemove} />
      </div>
    </div>
    <div className="work-detail-text">{reviewable && epoch
      ? <PlanReview epoch={epoch} projectId={item.projectId} path={item.file} sessions={reviewTo} onOpenSession={onOpenReviewSession} fallback={<WorkItemText hub={hub} item={item} />} reload={reload} />
      : <WorkItemText hub={hub} item={item} />}</div>
  </section>;
}
