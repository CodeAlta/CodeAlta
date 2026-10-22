import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { IssueFiltersRequest, IssueViewDetail, IssueViewName, IssueViewSaveRequest, IssueListRequest, IssueRow, IssueSource, WorkspaceProject } from "#neoastra";
import { IssuesPanel, type IssuesApi } from "./IssuesPanel";

// The Issues tab over a host the test plays: a repository on GitHub whose work is in three Jira projects, and a
// project that has one Jira project only.
const item = (id: string, title: string, values: Partial<IssueRow> = {}): IssueRow => ({ kind: "issue", id, title, url: `https://example.atlassian.net/browse/${id}`, state: "open", stateText: "To Do",
  type: "Task", priority: null, author: "Ana", assignees: [], labels: [], createdAt: null, updatedAt: null, comments: null, sourceBranch: null, targetBranch: null, ...values });
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
const filtersOf = (request: IssueFiltersRequest) => {
  if (request.service !== "azure_devops") return { status: "ok", filters: [], selected: {}, active: [], pick: [], problem: null, views: [], current: null };
  const view = state.views.find(candidate => candidate.name === state.current?.name && candidate.source === state.current?.source);
  const on = (view?.filters ?? []).filter(entry => entry.mode !== "off");
  const pick = on.filter(entry => entry.pick).map(entry => entry.id);
  // As the host: the window of the views asks without the current view; the tab, with it, where an active team the tab may not change wins.
  const viewTeam = request.withoutView ? undefined : on.find(entry => entry.id === "team" && !entry.pick && entry.value)?.value;
  const team = viewTeam === "Mobile" ? "t2" : viewTeam === "Platform" ? "t1" : request.selections?.team ?? "t1";
  const filters = [
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
  return { status: "ok", problem: null, views: state.views.map(({ name, source }) => ({ name, source })), current: state.current, filters, selected, active, pick };
};
const state = { lists: [] as IssueListRequest[], filterAsks: [] as IssueFiltersRequest[], views: [
    { name: "Bugs", source: "personal", path: ".alta/local/issues.toml", filters: [{ id: "type", mode: "on", value: "Bug", label: null, pick: null }] },
    { name: "Sprint board", source: "team", path: ".alta/issues.toml", filters: [{ id: "team", mode: "on", value: "Platform", label: null, pick: null },
      { id: "sprint", mode: "on", value: "@current", label: null, pick: true }, { id: "type", mode: "on", value: "Task", label: null, pick: null }] }] as IssueViewDetail[],
  current: { name: "Sprint board", source: "team" } as IssueViewName | null, saved: [] as IssueViewSaveRequest[], selects: [] as (string | null)[], problem: null as string | null, notices: [] as string[],
  // The starts the test ends itself, by the id of their item, and the sessions opened or offered when they end.
  starts: [] as string[], finish: {} as Record<string, (sessionId: string) => void>, opened: [] as string[], offered: [] as string[] };
const api = {
  sources: async (request: { projectId: string }) => ({ status: "ok", sources: sources[request.projectId] ?? [] }),
  list: async (request: IssueListRequest) => {
    state.lists.push(request);
    const all = request.service === "github" ? github : request.service === "azure_devops" ? work.filter(row => request.kind === "pull_request" || (request.filters?.team === "t2") === row.title.startsWith("Mobile"))
      : request.projectId === "single" ? [item("DOCS-1", "Explain the Jira projects")] : jira;
    return { status: "ok", items: all.filter(row => !request.part || row.id.startsWith(request.part + "-")), more: false, problem: state.problem, needsSignIn: false };
  },
  filters: async (request: IssueFiltersRequest) => { state.filterAsks.push(request); return filtersOf(request); },
  views: async () => ({ status: "ok", views: state.views, current: state.current, paths: [] }),
  saveView: async (request: IssueViewSaveRequest) => {
    state.saved.push(request);
    const path = request.source === "team" ? ".alta/issues.toml" : ".alta/local/issues.toml";
    state.views = [...state.views.filter(view => view.name !== request.name), { name: request.name, source: request.source, path, filters: request.filters ?? [] }];
    if (request.select) state.current = { name: request.name, source: request.source };
    return { status: "ok" };
  },
  fields: async () => ({ status: "ok", fields: [{ id: "field:Microsoft.VSTS.Common.Priority", label: "Priority", type: "list", name: "Microsoft.VSTS.Common.Priority" }] }),
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
  selectView: async (request: { name: string | null }) => {
    state.selects.push(request.name);
    const view = state.views.find(candidate => candidate.name === request.name);
    state.current = view ? { name: view.name, source: view.source } : null;
    return { status: "ok" };
  },
  read: async (request: { id: string }) => ({ status: "ok", item: [...jira, ...github, ...work].find(row => row.id === request.id) ?? null, body: `Description of ${request.id}.`, comments: [], moreComments: false, truncated: false }),
  start: (request: { id: string }) => new Promise(resolve => {
    state.starts.push(request.id);
    state.finish[request.id] = sessionId => resolve({ status: "ok", sessionId, reason: null, message: null });
  }),
  openLink: async () => ({ status: "ok" }),
} as unknown as IssuesApi;

const projects = [{ id: "mono", name: "Monorepo", path: "C:\\code\\mono", archived: false }, { id: "single", name: "Docs", path: "C:\\code\\docs", archived: false },
  { id: "ado", name: "Platform", path: "C:\\code\\platform", archived: false }] as WorkspaceProject[];
Object.assign(window, { issuesFixture: { state } });

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell" style={{ height: "100%" }}>
  <IssuesPanel api={api} epoch="epoch" projects={projects} projectId="mono" visible preferredStart="worktree" onActivate={() => { }}
    onOpenSession={id => { state.opened.push(id); }} onStarted={id => { state.offered.push(id); }}
    onNotice={message => { state.notices.push(message); }} />
</div></StrictMode>);
