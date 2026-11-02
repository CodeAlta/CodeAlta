import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type { IssueFiltersRequest, IssueViewDetail, IssueViewName, IssueViewSaveRequest, IssueListRequest, IssueRow, IssueSource, WorkspaceProject } from "#neoastra";
import { IssuesPanel, type IssuesApi } from "./IssuesPanel";

// The Issues tab over a host the test plays: a repository on GitHub whose work is in three Jira projects, and a
// project that has one Jira project only.
const item = (id: string, title: string, values: Partial<IssueRow> = {}): IssueRow => ({ kind: "issue", id, title, url: `https://example.atlassian.net/browse/${id}`, state: "open", stateText: "To Do",
  type: "Task", priority: null, author: "Ana", assignees: [], labels: [], createdAt: null, updatedAt: null, comments: null, sourceBranch: null, targetBranch: null, place: null, match: null, reviewers: [], ...values });
const jira: IssueRow[] = [
  item("PLAT-7", "The build cache is not shared between agents", { type: "Story", stateText: "In Progress", labels: [{ name: "build", color: null }] }),
  item("ALTA-12", "The list loses its place after a reload", { type: "Bug" }),
  item("TEAM-3", "Write the onboarding page"),
  item("ALTA-9", "Keep the filter when the tab is opened again"),
  item("PLAT-2", "Move the runners to the new pool", { author: "Bo" }),
];
const github: IssueRow[] = [item("128", "Crash on start", { url: "https://github.com/o/r/issues/128", stateText: null, type: null })];
const sources: Record<string, IssueSource[]> = {
  mono: [
    { service: "github", name: "GitHub", location: "o/r", url: "https://github.com/o/r", kinds: ["issue", "pull_request"], parts: [] },
    { service: "jira", name: "Jira", location: "ALTA, PLAT, TEAM", url: "https://example.atlassian.net/issues/?jql=project", kinds: ["issue"], parts: ["ALTA", "PLAT", "TEAM"] },
  ],
  single: [{ service: "jira", name: "Jira", location: "DOCS", url: "https://example.atlassian.net/browse/DOCS", kinds: ["issue"], parts: [] }],
  ado: [{ service: "azure_devops", name: "Azure DevOps", location: "org/proj/app", url: "https://dev.azure.com/org/proj/_git/app", kinds: ["issue", "pull_request"], parts: [] }],
};
// The work items of an Azure DevOps project, by team; the view of the team lists those of the team Platform in the current
// sprint, and the user has a view of their own. The host gives an active filter of the current view its value, as here.
const work: IssueRow[] = [item("41", "Platform work", { url: "https://dev.azure.com/org/proj/_workitems/edit/41" }), item("42", "Mobile work", { url: "https://dev.azure.com/org/proj/_workitems/edit/42" })];
// The pull requests of the project: one waits for a review of a group of the user, one is of another repository of the project.
const pulls: IssueRow[] = [
  item("7", "Cache the build", { kind: "pull_request", url: "https://dev.azure.com/org/proj/_git/app/pullrequest/7", stateText: null, type: null,
    reviewers: [{ name: "Core", vote: "none", group: true, required: true, mine: true }, { name: "Bo", vote: "approved", group: null, required: null, mine: null }] }),
  item("9", "New fields", { kind: "pull_request", url: "https://dev.azure.com/org/proj/_git/other/pullrequest/9", stateText: null, type: null, place: "proj/other",
    reviewers: [{ name: "Fields", vote: "waiting", group: true, required: true, mine: true }] }),
];
// The filters of the pull requests, as Azure DevOps gives them.
const pullFilters = [
  { id: "scope", label: "Scope", values: [{ id: "@project", label: "This project", detail: "Every repository of the project" }, { id: "@organization", label: "Organization", detail: "Every project" }],
    dependsOn: [], problem: null, trackerLabel: "Scope", kind: "choice", fieldType: null, allLabel: "This repository" },
  { id: "reviewer", label: "Reviewer", values: [{ id: "@me", label: "Me", detail: null }, { id: "@me_groups", label: "Me or my groups", detail: "Also through groups in groups" }],
    dependsOn: [], problem: null, trackerLabel: "Reviewer", kind: "choice", fieldType: null },
  { id: "vote", label: "Vote", values: [{ id: "@none", label: "No vote yet", detail: null }, { id: "@approved", label: "Approved", detail: null }],
    dependsOn: [], problem: null, trackerLabel: "Vote", kind: "choice", fieldType: null, multiple: true },
];
// The views and the current one of a kind of items: those of the pull requests are their own.
const store = (kind: string | null | undefined) => kind === "pull_request" ? state.pulls : state;
const filtersOf = (request: IssueFiltersRequest) => {
  // As the host: a value longer than 128 characters is refused, a tracker that fails answers without its filters, and the
  // problem of a file of views that does not parse comes with the filters.
  const chosen = Object.values(request.selections ?? {});
  if (chosen.some(value => value.length > 128)) return { status: "invalid_request", filters: [], selected: {}, active: [], pick: [], problem: null, views: [], current: null };
  if (state.failFilters || state.failPicks && chosen.length > 0) return { status: "ok", filters: [], selected: {}, active: [], pick: [],
    problem: ["Azure DevOps did not give its filters.", state.viewsProblem].filter(text => text).join(" "), views: state.views.map(({ name, source }) => ({ name, source })), current: state.current };
  if (request.service !== "azure_devops") return { status: "ok", filters: [], selected: {}, active: [], pick: [], problem: null, views: [], current: null };
  const kept = store(request.kind);
  const view = kept.views.find(candidate => candidate.name === kept.current?.name && candidate.source === kept.current?.source);
  const on = (view?.filters ?? []).filter(entry => entry.mode !== "off");
  const pick = on.filter(entry => entry.pick).map(entry => entry.id);
  // As the host: the window of the views asks without the current view; the tab, with it, where an active team the tab may not change wins.
  const viewTeam = request.withoutView ? undefined : on.find(entry => entry.id === "team" && !entry.pick && entry.value)?.value;
  const team = viewTeam === "Mobile" ? "t2" : viewTeam === "Platform" ? "t1" : request.selections?.team ?? "t1";
  const filters = request.kind === "pull_request" ? pullFilters : [
    { id: "team", label: "Team", values: [{ id: "t1", label: "Platform", detail: "Your team" }, { id: "t2", label: "Mobile", detail: null }], dependsOn: [], problem: null,
      trackerLabel: "Team", kind: "choice", fieldType: null },
    { id: "sprint", label: "Sprint", values: [{ id: "@current", label: "Current sprint", detail: `S12 of ${team}` }, { id: `s-${team}`, label: `S11 of ${team}`, detail: null }],
      dependsOn: ["team"], problem: null, trackerLabel: "Sprint", kind: "choice", fieldType: null },
    { id: "type", label: "Type", values: ["Task", "Bug"].map(type => ({ id: type, label: type, detail: null })), dependsOn: [], problem: null, trackerLabel: "Type", kind: "choice", fieldType: null, multiple: true },
    { id: "field:Custom.Customer", label: "Customer", values: [], dependsOn: [], problem: null, trackerLabel: "Customer", kind: "text", fieldType: "text" },
    { id: "field:System.State", label: "State", values: ["New", "Ready", "On Hold"].map(state => ({ id: state, label: state, detail: null })), dependsOn: [], problem: null,
      trackerLabel: "State", kind: "choice", fieldType: "list", multiple: true },
    { id: "sort", label: "Order", values: [{ id: "backlog", label: "Backlog order", detail: null }], dependsOn: [], problem: null, trackerLabel: "Order", kind: "choice", fieldType: null,
      allLabel: "Last changed first" },
  ];
  // A value of the view by its id or its label.
  const idOf = (id: string, value: string) => filters.find(filter => filter.id === id)?.values.find(candidate => candidate.id === value || candidate.label === value)?.id ?? value;
  const selected = Object.fromEntries(filters.map(filter => [filter.id, ""]));
  for (const entry of on) selected[entry.id] = (entry.value ?? "").split("\u001f").filter(value => value).map(value => idOf(entry.id, value)).join("\u001f");
  // A filter the view lets the tab change takes the value chosen there.
  for (const id of pick) if (request.selections && id in request.selections) selected[id] = request.selections[id];
  const active = [...pick, ...on.filter(entry => !entry.pick && selected[entry.id]).map(entry => entry.id)];
  return { status: "ok", problem: state.viewsProblem, views: kept.views.map(({ name, source }) => ({ name, source })), current: kept.current, filters, selected, active, pick };
};
// The descriptions with images: Markdown in a pull request, HTML in a work item; one image cannot be read.
const pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
const bodies: Record<string, string> = {
  "7": "A cache.\n\n![shot.png](https://dev.azure.com/org/proj/_apis/git/repositories/app/pullRequests/7/attachments/shot.png)\n\n![missing](https://dev.azure.com/org/proj/_apis/wit/attachments/gone)",
  "41": '<div>Steps:</div><div><a href="https://dev.azure.com/org/proj/_apis/wit/attachments/1?fileName=big.png"><img src="https://dev.azure.com/org/proj/_apis/wit/attachments/1?fileName=image.png" alt="Image"></a></div><img src="javascript:alert(1)">',
};
const state = { images: [] as { address: string; projectId: string; service: string }[], reads: [] as { id: string; fresh?: boolean | null }[], lists: [] as IssueListRequest[], filterAsks: [] as IssueFiltersRequest[], views: [
    { name: "Bugs", source: "personal", path: ".alta/local/issues.toml", filters: [{ id: "type", mode: "on", value: "Bug", label: null, pick: null }] },
    { name: "Sprint board", source: "team", path: ".alta/issues.toml", filters: [{ id: "team", mode: "on", value: "Platform", label: null, pick: null },
      { id: "sprint", mode: "on", value: "@current", label: null, pick: true }, { id: "type", mode: "on", value: "Task", label: null, pick: null }] }] as IssueViewDetail[],
  current: { name: "Sprint board", source: "team" } as IssueViewName | null,
  pulls: { views: [{ name: "Waiting for me", source: "personal", path: ".alta/local/issues.toml", filters: [{ id: "scope", mode: "on", value: "@project", label: null, pick: true },
    { id: "reviewer", mode: "on", value: "@me_groups", label: null, pick: null }, { id: "vote", mode: "on", value: "@none", label: null, pick: null }] }] as IssueViewDetail[],
    current: { name: "Waiting for me", source: "personal" } as IssueViewName | null },
  saved: [] as IssueViewSaveRequest[], selects: [] as (string | null)[], problem: null as string | null, notices: [] as string[],
  // The tracker fails when values are chosen in the tab, or always; the request of the filters fails; the replies of the filters
  // wait while paused; the views read, and the problem of a file of views.
  failPicks: false, failFilters: false, throwFilters: false, paused: false, waiting: [] as (() => void)[], viewReads: 0, viewsProblem: null as string | null,
  // The starts the test ends itself, by the id of their item, and the sessions opened or offered when they end.
  starts: [] as string[], finish: {} as Record<string, (sessionId: string) => void>, opened: [] as string[], offered: [] as string[] };
const api = {
  sources: async (request: { projectId: string }) => ({ status: "ok", sources: sources[request.projectId] ?? [] }),
  list: async (request: IssueListRequest) => {
    state.lists.push(request);
    const all = request.service === "github" ? github : request.service === "azure_devops" ? request.kind === "pull_request" ? pulls : work.filter(row => (request.filters?.team === "t2") === row.title.startsWith("Mobile"))
      : request.projectId === "single" ? [item("DOCS-1", "Explain the Jira projects")] : jira;
    return { status: "ok", items: all.filter(row => !request.part || row.id.startsWith(request.part + "-")), more: false, problem: state.problem, needsSignIn: false,
      readAt: new Date(Date.now() - (request.fresh ? 0 : 3 * 60_000)).toISOString() };
  },
  filters: async (request: IssueFiltersRequest) => {
    state.filterAsks.push(request);
    if (state.paused) await new Promise<void>(resolve => state.waiting.push(resolve));
    if (state.throwFilters) throw new Error("The host did not answer.");
    return filtersOf(request);
  },
  views: async (request: { kind?: string | null }) => { state.viewReads++; const kept = store(request.kind); return { status: "ok", views: kept.views, current: kept.current, paths: [], problem: state.viewsProblem }; },
  saveView: async (request: IssueViewSaveRequest) => {
    state.saved.push(request);
    const kept = store(request.kind);
    const path = request.source === "team" ? ".alta/issues.toml" : ".alta/local/issues.toml";
    kept.views = [...kept.views.filter(view => view.name !== request.name), { name: request.name, source: request.source, path, filters: request.filters ?? [] }];
    if (request.select) kept.current = { name: request.name, source: request.source };
    return { status: "ok" };
  },
  fields: async (request: { kind?: string | null }) => ({ status: "ok",
    fields: request.kind === "pull_request" ? [] : [{ id: "field:Microsoft.VSTS.Common.Priority", label: "Priority", type: "list", name: "Microsoft.VSTS.Common.Priority" }] }),
  renameView: async (request: { name: string; newName: string; source: string }) => {
    state.views = state.views.map(view => view.name === request.name && view.source === request.source ? { ...view, name: request.newName } : view);
    if (state.current?.name === request.name) state.current = { name: request.newName, source: request.source };
    return { status: "ok" };
  },
  removeView: async (request: { name: string; source: string }) => {
    state.views = state.views.filter(view => view.name !== request.name || view.source !== request.source);
    if (state.current?.name === request.name) state.current = null;
    return { status: "ok" };
  },
  selectView: async (request: { name: string | null; kind?: string | null }) => {
    state.selects.push(request.name);
    const kept = store(request.kind);
    const view = kept.views.find(candidate => candidate.name === request.name);
    kept.current = view ? { name: view.name, source: view.source } : null;
    return { status: "ok" };
  },
  read: async (request: { id: string; fresh?: boolean | null }) => (state.reads.push(request), { status: "ok", readAt: new Date(Date.now() - (request.fresh ? 0 : 2 * 60_000)).toISOString(),
    item: [...jira, ...github, ...work, ...pulls].find(row => row.id === request.id) ?? null, body: bodies[request.id] ?? `Description of ${request.id}.`,
    comments: request.id === "7" ? [{ author: "Bo", createdAt: null, body: "See ![again](https://dev.azure.com/org/proj/_apis/git/repositories/app/pullRequests/7/attachments/shot.png)" }] : [],
    moreComments: false, truncated: false }),
  image: async (request: { address: string; projectId: string; service: string }) => (state.images.push(request),
    request.address.includes("/gone") ? { status: "not_found", mediaType: null, data: null } : { status: "ok", mediaType: "image/png", data: pixel }),
  start: (request: { id: string }) => new Promise(resolve => {
    state.starts.push(request.id);
    state.finish[request.id] = sessionId => resolve({ status: "ok", sessionId, reason: null, message: null });
  }),
  openLink: async () => ({ status: "ok" }),
} as unknown as IssuesApi;

const projects = [{ id: "mono", name: "Monorepo", path: "C:\\code\\mono", archived: false }, { id: "single", name: "Docs", path: "C:\\code\\docs", archived: false },
  { id: "ado", name: "Platform", path: "C:\\code\\platform", archived: false }] as WorkspaceProject[];
// The shell renders the tab again on its own events, with the same api: `rerender` does as it does.
const fixture = { state, rerender: () => { } };
Object.assign(window, { issuesFixture: fixture });

function Shell() {
  const [, setRenders] = useState(0);
  useEffect(() => { fixture.rerender = () => setRenders(value => value + 1); }, []);
  return <div className="ide-shell" style={{ height: "100%" }}>
    <IssuesPanel api={api} epoch="epoch" projects={projects} projectId="mono" visible preferredStart="worktree" onActivate={() => { }}
      onOpenSession={id => { state.opened.push(id); }} onStarted={id => { state.offered.push(id); }}
      onNotice={message => { state.notices.push(message); }} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Shell /></StrictMode>);
