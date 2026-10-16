import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { IssueListRequest, IssueRow, IssueSource, WorkspaceProject } from "#neoastra";
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
};
const state = { lists: [] as IssueListRequest[], problem: null as string | null, notices: [] as string[],
  // The starts the test ends itself, by the id of their item, and the sessions opened or offered when they end.
  starts: [] as string[], finish: {} as Record<string, (sessionId: string) => void>, opened: [] as string[], offered: [] as string[] };
const api = {
  sources: async (request: { projectId: string }) => ({ status: "ok", sources: sources[request.projectId] ?? [] }),
  list: async (request: IssueListRequest) => {
    state.lists.push(request);
    const all = request.service === "github" ? github : request.projectId === "single" ? [item("DOCS-1", "Explain the Jira projects")] : jira;
    return { status: "ok", items: all.filter(row => !request.part || row.id.startsWith(request.part + "-")), more: false, problem: state.problem, needsSignIn: false };
  },
  read: async (request: { id: string }) => ({ status: "ok", item: [...jira, ...github].find(row => row.id === request.id) ?? null, body: `Description of ${request.id}.`, comments: [], moreComments: false, truncated: false }),
  start: (request: { id: string }) => new Promise(resolve => {
    state.starts.push(request.id);
    state.finish[request.id] = sessionId => resolve({ status: "ok", sessionId, reason: null, message: null });
  }),
  openLink: async () => ({ status: "ok" }),
} as unknown as IssuesApi;

const projects = [{ id: "mono", name: "Monorepo", path: "C:\\code\\mono", archived: false }, { id: "single", name: "Docs", path: "C:\\code\\docs", archived: false }] as WorkspaceProject[];
Object.assign(window, { issuesFixture: { state } });

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell" style={{ height: "100%" }}>
  <IssuesPanel api={api} epoch="epoch" projects={projects} projectId="mono" visible preferredStart="worktree" onActivate={() => { }}
    onOpenSession={id => { state.opened.push(id); }} onStarted={id => { state.offered.push(id); }}
    onNotice={message => { state.notices.push(message); }} />
</div></StrictMode>);
