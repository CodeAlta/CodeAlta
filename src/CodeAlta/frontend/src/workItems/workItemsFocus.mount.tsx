import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkItemRow, WorkspaceProject } from "#neoastra";
import { WorkItemsPanel } from "./WorkItemsPanel";
import { defaultWorkSettings, workItemKey, type WorkItemsFocus } from "./workItems";
import type { WorkItemsHub, WorkItemsState } from "./workItemsHub";

// Forty tasks of one project, so that the list scrolls: the page asks the tab to show the last one, as another part of the window does.
const row = (index: number): WorkItemRow => ({ id: `task-${index}`, kind: "task", title: `Task ${index}`, summary: null, category: "problem", status: "pending", statusText: null,
  created: "2026-11-10", file: `.alta/tasks/task-${index}.md`, proposedBy: null, runner: null, acknowledged: false, runsWith: null, tags: null, shortId: null, updated: null });
const value: WorkItemsState = { loaded: true, complete: true, available: true, settings: defaultWorkSettings,
  projects: [{ projectId: "p", truncated: false, tags: null, declinedTags: null, tagProposals: null, plans: [], tasks: Array.from({ length: 40 }, (_, index) => row(index + 1)) }] };
const hub = { subscribe: () => () => { }, getSnapshot: () => value, connect: () => { }, setProjects: () => { }, refresh: async () => { },
  read: () => new Promise(() => { }), act: async () => ({ ok: true }), saveSettings: async () => { } } as unknown as WorkItemsHub;
const project = { id: "p", name: "Alpha", path: "C:\code\alpha", archived: false } as WorkspaceProject;
const never = () => { };

const fixture = { key: (index: number) => workItemKey({ projectId: "p", kind: "task", id: `task-${index}` }), focus: (_: WorkItemsFocus) => { } };
Object.assign(window, { focusFixture: fixture });

function Page() {
  const [focus, setFocus] = useState<WorkItemsFocus | null>(null);
  fixture.focus = next => setFocus(next);
  return <div style={{ height: 600, display: "flex", flexDirection: "column" }}>
    <WorkItemsPanel hub={hub} projects={[project]} sessions={[]} runningSessions={new Set()} projectId="p" visible focus={focus} onFocused={() => setFocus(null)}
      onActivate={never} onStart={async () => false} onOpenSession={never} onOpenFile={never} onOpenSettings={never} />
  </div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Page /></StrictMode>);
