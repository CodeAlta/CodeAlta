import { useEffect, useId, useMemo, useRef, useState } from "react";
import { Button, Callout, HTMLSelect, InputGroup, SegmentedControl, Tab, Tabs, Tag } from "@blueprintjs/core";
import type { issues as issuesService, IssueReadResponse, IssueRow, IssueSource, WorkspaceProject } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { BrandIcon } from "../BrandIcon";
import { serviceBrand } from "../brands";
import { AppWindow } from "../AppWindow";
import type { MessageKey } from "../localization";
import { MarkdownContent } from "../MarkdownContent";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { WorkStartButtons } from "../workItems/WorkItemCards";
import { startRefusal } from "../workItems/startWorkItem";
import type { WorkStart } from "../workItems/workItems";
import { issueFilterLabel, issueFilters, issueIcon, issueKey, issueReference, issueStateLabel, issueTone, kindLabel, labelColors, sameList, signInCommand, sourceKinds,
  type IssueFilter, type IssueKind, type IssueListKey } from "./issues";

/** The part of the host the tab uses; a test gives its own. */
export type IssuesApi = Pick<typeof issuesService, "sources" | "list" | "read" | "start" | "openLink">;

type Listing = Readonly<{ key: IssueListKey; loading: boolean; items: readonly IssueRow[]; more: boolean; problem: string | null; needsSignIn: boolean }>;
type Sources = Readonly<{ projectId: string; loading: boolean; sources: readonly IssueSource[] }>;
type Detail = Readonly<{ key: string; loading: boolean; value: IssueReadResponse | null }>;

/**
 * The Issues tab: the issues and the pull requests of a project, from the service that hosts its repository
 * (GitHub, GitLab, Azure DevOps, Bitbucket) and from the trackers plugins add (Jira). One list at a time (open,
 * closed, merged, all) with what is typed as a filter; an item is read on the right, opened in a window or on the
 * web, and a session can be started on it.
 */
export function IssuesPanel({ api, epoch, projects, projectId, visible, preferredStart, onActivate, onOpenSession, onNotice }: {
  api: IssuesApi;
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
  /** Says something that went wrong, outside the tab. */
  onNotice: (message: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [scope, setScope] = useState<string | null>(projectId ?? projects[0]?.id ?? null);
  const [sources, setSources] = useState<Sources | null>(null);
  const [service, setService] = useState<string | null>(null);
  const [kind, setKind] = useState<IssueKind>("issue");
  const [filter, setFilter] = useState<IssueFilter>("open");
  const [typed, setTyped] = useState("");
  const [query, setQuery] = useState("");
  const [listing, setListing] = useState<Listing | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [expanded, setExpanded] = useState(false);
  const [starting, setStarting] = useState(false);
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
  const wanted = useMemo<IssueListKey | null>(() => project && source ? { projectId: project.id, service: source.service, kind: shownKind, filter: shownFilter, query } : null,
    [project?.id, source?.service, shownKind, shownFilter, query]);

  useEffect(() => {
    if (!available || !visible || !wanted) return;
    const controller = new AbortController();
    setListing(current => sameList(current?.key ?? null, wanted) ? { ...current!, loading: true } : { key: wanted, loading: true, items: [], more: false, problem: null, needsSignIn: false });
    void api.list({ expectedEpoch: epoch, projectId: wanted.projectId, service: wanted.service, kind: wanted.kind, filter: wanted.filter, query: wanted.query || null, limit: 50 },
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
  const start = async (item: IssueRow, way: WorkStart) => {
    if (!available || !wanted || starting) return;
    setStarting(true);
    try {
      const reply = await api.start({ expectedEpoch: epoch, projectId: wanted.projectId, service: wanted.service, kind: item.kind, id: item.id, worktree: way === "worktree", sessionId: null },
        { timeoutMilliseconds: 600_000 });
      if (reply.sessionId) { setExpanded(false); onOpenSession(reply.sessionId, wanted.projectId); }
      if (reply.status !== "ok") { const known = startRefusal(reply.reason); onNotice(known ? t(known) : reply.message ?? t("The work did not start.")); }
    } catch { onNotice(t("The work did not start.")); }
    finally { setStarting(false); }
  };

  if (!available) return <div className="work-page issues-page" onPointerDown={onActivate}><p className="work-empty">{t("Issues are unavailable in this window.")}</p></div>;
  if (!project) return <div className="work-page issues-page" onPointerDown={onActivate}><p className="work-empty">{t("No projects in this snapshot.")}</p></div>;
  const loadingSources = !sources || sources.projectId !== project?.id || sources.loading && known.length === 0;
  const noun = kindLabel(shownKind, source?.service, true);
  const command = source ? signInCommand(source.service) : null;
  const read = detail?.key === currentKey ? detail : null;
  const body = current && <IssueBody item={read?.value?.item ?? current} read={read} service={source?.service ?? null} />;
  const actions = current && <IssueActions item={current} service={source?.name ?? ""} preferredStart={preferredStart} starting={starting} onStart={way => void start(current, way)}
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
        : source && <span className="issues-source" title={source.location}>{serviceBrand(source.service) && <BrandIcon name={serviceBrand(source.service)!} size={15} />}<strong>{source.name}</strong><span>{source.location}</span></span>}
      {kinds.length > 1 && <SegmentedControl size="small" value={shownKind} onValueChange={value => { setKind(value as IssueKind); setSelected(null); }}
        options={kinds.map(candidate => ({ value: candidate, label: t(kindLabel(candidate, source?.service, true)) }))} />}
      <InputGroup size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={typed} disabled={!source}
        placeholder={t("Filter by words or number")} aria-label={t("Filter by words or number")} onChange={event => setTyped(event.target.value)} />
    </div>
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
        {shown?.problem && <Callout intent={shown.needsSignIn ? "warning" : "danger"} compact role="alert" className="work-notice issues-notice">
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

function IssueActions({ item, service, preferredStart, starting, onStart, onOpen, onCopy }: {
  item: IssueRow; service: string; preferredStart: string; starting: boolean; onStart: (way: WorkStart) => void; onOpen: () => void; onCopy: () => void;
}) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => { if (!copied) return; const timer = setTimeout(() => setCopied(false), 1500); return () => clearTimeout(timer); }, [copied]);
  return <>
    {(item.state === "open" || item.state === "draft") && <WorkStartButtons preferred={preferredStart === "session" ? "session" : "worktree"} here={false} disabled={starting} onStart={onStart} />}
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
