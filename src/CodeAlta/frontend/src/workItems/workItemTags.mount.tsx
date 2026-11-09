import { StrictMode, useState, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import type { WorkItemRow, WorkItemTagRow } from "#neoastra";
import { TagManagerDialog, TaskTagField } from "./WorkItemTagViews";
import { workItems } from "./workItems";
import type { WorkItemsHub, WorkItemTarget, WorkTagAction } from "./workItemsHub";

// A task of project p1 with the tag mobile, the tags of p1, and a hub that keeps them as the host would: the calls are a list.
const state = {
  calls: [] as string[],
  tags: ["mobile"] as string[],
  projectTags: [{ name: "mobile", description: "The phone app.", openTasks: 1, listed: true }, { name: "flaky-tests", description: "A test that fails at random.", openTasks: 0, listed: true },
    { name: "handmade", description: null, openTasks: 1, listed: false }] as WorkItemTagRow[],
  declined: ["ci-noise"] as string[],
};
Object.assign(window, { tagsFixture: state });
const listeners = new Set<() => void>();
let version = 0;
const changed = () => { version++; for (const listener of [...listeners]) listener(); };
const ok = { ok: true, status: "ok", message: null, reason: null, sessionId: null, prompt: null, agentPromptId: null };
const hub = {
  subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
  getSnapshot: () => version,
  async tagTask(_target: WorkItemTarget, tags: readonly string[]) { state.calls.push(`task:${tags.join(",")}`); state.tags = [...tags]; changed(); return ok; },
  async tag(_projectId: string, action: WorkTagAction, name: string, value: string | null = null) {
    state.calls.push(`${action}:${name}:${value ?? ""}`);
    if (action === "rename" && state.projectTags.some(tag => tag.name === value)) return "exists";
    if (action === "set") {
      const known = state.projectTags.find(tag => tag.name === name);
      state.projectTags = known ? state.projectTags.map(tag => tag === known ? { ...tag, description: value, listed: true } : tag)
        : [...state.projectTags, { name, description: value, openTasks: 0, listed: true }];
    }
    if (action === "rename") { state.projectTags = state.projectTags.map(tag => tag.name === name ? { ...tag, name: value! } : tag); state.tags = state.tags.map(tag => tag === name ? value! : tag); }
    if (action === "delete") { state.projectTags = state.projectTags.filter(tag => tag.name !== name); state.tags = state.tags.filter(tag => tag !== name); }
    if (action === "forget") state.declined = state.declined.filter(other => other !== name);
    changed();
    return "ok";
  },
} as unknown as WorkItemsHub;

const row = (): WorkItemRow => ({ id: "2026-11-10-fix-the-parser-p53b", kind: "task", title: "Fix the parser", summary: null, category: "problem", status: "pending", statusText: null,
  created: "2026-11-10", file: ".alta/tasks/2026-11-10-fix-the-parser-p53b.md", proposedBy: null, runner: null, acknowledged: false, runsWith: null, tags: state.tags, shortId: "p53b", updated: null });

function Page() {
  useSyncExternalStore(hub.subscribe, hub.getSnapshot as unknown as () => number);
  const [managing, setManaging] = useState(false);
  const [item] = workItems([{ projectId: "p1", truncated: false, plans: [], tasks: [row()], tags: state.projectTags, declinedTags: state.declined }], new Set());
  return <div style={{ padding: 24, width: 640 }}>
    <TaskTagField hub={hub} item={item} tags={state.projectTags} disabled={false} />
    <button type="button" id="manage" onClick={() => setManaging(true)}>Manage</button>
    {managing && <TagManagerDialog hub={hub} projectId="p1" onClose={() => setManaging(false)}
      projects={[{ id: "p1", name: "Alpha", tags: state.projectTags, declined: state.declined }]} />}
  </div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Page /></StrictMode>);
