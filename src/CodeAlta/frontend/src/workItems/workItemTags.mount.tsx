import { StrictMode, useState, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import type { WorkItemRow, WorkItemTagProposalRow, WorkItemTagRow } from "#neoastra";
import { TagManagerDialog, TagProposalBanners, TagProposalDialog, TaskTagField, type ShownTagProposal } from "./WorkItemTagViews";
import { workItems } from "./workItems";
import type { WorkItemsHub, WorkItemTarget, WorkTagAction } from "./workItemsHub";

// A task of project p1 with the tag mobile, the tags of p1, and a hub that keeps them as the host would: the calls are a list.
const state = {
  calls: [] as string[],
  // While held, a save of the task's tags waits for release(), as a slow host would.
  held: false,
  waiting: [] as (() => void)[],
  release() { state.held = false; for (const resolve of state.waiting.splice(0)) resolve(); },
  // The clicks that reached the button under the picker.
  behind: 0,
  tags: ["mobile"] as string[],
  projectTags: [{ name: "mobile", description: "The phone app.", openTasks: 1, listed: true }, { name: "flaky-tests", description: "A test that fails at random.", openTasks: 0, listed: true },
    { name: "handmade", description: null, openTasks: 1, listed: false }] as WorkItemTagRow[],
  declined: ["ci-noise"] as string[],
  // An agent proposes the new tag parser for the task, and for another one.
  proposals: [{ name: "parser", description: "The parser of the files.", newTag: true, sessionId: "s1",
    tasks: [{ id: "2026-11-10-fix-the-parser-p53b", reason: "fixes the parser" }, { id: "2026-11-09-lex-the-numbers-q9rh", reason: "the lexer feeds it" }] }] as WorkItemTagProposalRow[],
};
Object.assign(window, { tagsFixture: state });
const listeners = new Set<() => void>();
let version = 0;
const changed = () => { version++; for (const listener of [...listeners]) listener(); };
const ok = { ok: true, status: "ok", message: null, reason: null, sessionId: null, prompt: null, agentPromptId: null };
const hub = {
  subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
  getSnapshot: () => version,
  async findTagTasks(_projectId: string, name: string) { state.calls.push(`find:${name}`); return ok; },
  async tagTask(_target: WorkItemTarget, tags: readonly string[]) { state.calls.push(`task:${tags.join(",")}`);
    if (state.held) await new Promise<void>(resolve => state.waiting.push(resolve));
    state.tags = [...tags]; changed(); return ok; },
  async tag(_projectId: string, action: WorkTagAction, name: string, value: string | null = null, tasks: readonly string[] | null = null) {
    state.calls.push(`${action}:${name}:${value ?? ""}${tasks ? `:${tasks.join(",")}` : ""}`);
    if (action === "apply" || action === "dismiss") state.proposals = state.proposals.filter(proposal => proposal.name !== name);
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

const row = (id = "2026-11-10-fix-the-parser-p53b", title = "Fix the parser"): WorkItemRow => ({ id, kind: "task", title, summary: null, category: "problem", status: "pending",
  statusText: null, created: "2026-11-10", file: `.alta/tasks/${id}.md`, proposedBy: null, runner: null, acknowledged: false, runsWith: null,
  tags: id.endsWith("p53b") ? state.tags : null, shortId: id.slice(-4), updated: null });

function Page() {
  useSyncExternalStore(hub.subscribe, hub.getSnapshot as unknown as () => number);
  const [managing, setManaging] = useState(false);
  const [reviewing, setReviewing] = useState<ShownTagProposal | null>(null);
  const items = workItems([{ projectId: "p1", truncated: false, plans: [], tasks: [row(), row("2026-11-09-lex-the-numbers-q9rh", "Lex the numbers")], tags: state.projectTags,
    declinedTags: state.declined, tagProposals: state.proposals }], new Set());
  const item = items.find(task => task.id.endsWith("p53b"))!;
  const proposals = state.proposals.map(proposal => ({ projectId: "p1", projectName: null, proposal, tasks: items.filter(task => proposal.tasks.some(match => match.id === task.id)) }));
  return <div style={{ padding: 24, width: 640 }}>
    <TagProposalBanners proposals={proposals} onReview={setReviewing} onDismiss={shown => void hub.tag(shown.projectId, "dismiss", shown.proposal.name)} />
    {reviewing && <TagProposalDialog hub={hub} shown={reviewing} onClose={() => setReviewing(null)} />}
    <TaskTagField hub={hub} item={item} tags={state.projectTags} disabled={false} />
    <button type="button" id="behind" style={{ display: "block", marginTop: 8, marginLeft: 360 }} onClick={() => { state.behind++; }}>Behind the picker</button>
    <button type="button" id="manage" onClick={() => setManaging(true)}>Manage</button>
    {managing && <TagManagerDialog hub={hub} projectId="p1" onClose={() => setManaging(false)}
      projects={[{ id: "p1", name: "Alpha", tags: state.projectTags, declined: state.declined }]} />}
  </div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Page /></StrictMode>);
