import assert from "node:assert/strict";
import test from "node:test";
import type { WorkItemRow, WorkItemsListResponse, WorkItemsProject, WorkItemsSettings } from "#neoastra";
import { startRefusal, startWorkItem, type WorkStartHost } from "./startWorkItem";
import { defaultWorkSettings, workItems, type WorkItem } from "./workItems";
import { createWorkItemsHub, readingBatches, type WorkItemsApi } from "./workItemsHub";

const row = (id: string, more: Partial<WorkItemRow> = {}): WorkItemRow => ({ id, kind: "task", title: id, summary: null, category: "gap", status: "pending", statusText: null,
  created: null, file: `.alta/tasks/${id}.md`, proposedBy: null, runner: null, acknowledged: false, runsWith: null, ...more });
const settled = () => new Promise<void>(resolve => setImmediate(resolve));

/** A host whose projects each have what the test says, and that records what it is asked. */
function host(found: Record<string, WorkItemRow[]>) {
  const asked: (readonly string[] | null)[] = [];
  const acted: string[] = [];
  const ranWith: (string | null)[][] = [];
  let settings: WorkItemsSettings = defaultWorkSettings;
  let tell: (() => void) | null = null;
  const pending: (() => void)[] = [];
  let hold = false;
  const api: WorkItemsApi = {
    async list(request) {
      asked.push(request.projectIds ?? null);
      if (hold) await new Promise<void>(resolve => pending.push(resolve));
      const projects: WorkItemsProject[] = (request.projectIds ?? Object.keys(found)).filter(id => found[id]?.length)
        .map(id => ({ projectId: id, tasks: found[id], plans: [], truncated: false }));
      return { status: request.expectedEpoch === "epoch" ? "ok" : "stale_epoch", projects, settings } satisfies WorkItemsListResponse;
    },
    async read() { return { status: "ok", markdown: "Text.", truncated: false }; },
    async act(request) {
      acted.push(`${request.action}:${request.id}:${request.sessionId ?? ""}`);
      if (request.action.startsWith("start_")) ranWith.push([request.providerId, request.modelId, request.reasoningEffort]);
      if (request.action === "dismiss") found[request.projectId] = found[request.projectId].filter(item => item.id !== request.id);
      return request.action === "start_here" ? { status: "ok", sessionId: request.sessionId, message: null, reason: null, prompt: "Do it.", agentPromptId: request.kind === "plan" ? "default" : null }
        : request.action === "start_worktree" ? { status: "refused", sessionId: null, message: "git said no", reason: "worktree_not_repository", prompt: null, agentPromptId: null }
        : { status: "ok", sessionId: request.action === "start_session" ? "new" : null, message: null, reason: null, prompt: null, agentPromptId: null };
    },
    async saveSettings(request) { settings = request.settings!; return { status: "ok", settings }; },
    async watch(_request, options) {
      return (async function* () {
        yield { revision: 0 };
        while (!options?.signal?.aborted) {
          await new Promise<void>(resolve => { tell = resolve; options?.signal?.addEventListener("abort", () => resolve(), { once: true }); });
          if (!options?.signal?.aborted) yield { revision: 1 };
        }
      })();
    },
  } as WorkItemsApi;
  return { api, asked, acted, ranWith, change: () => tell?.(), holding: (value: boolean) => { hold = value; }, release: () => pending.shift()?.() };
}
const noTimers = { set: () => 0, clear: () => {} };

test("a reading asks for a few projects at a time, the first ones first", () => {
  const ids = Array.from({ length: 50 }, (_, index) => `p${index}`);
  const batches = readingBatches(ids);
  assert.deepEqual(batches.map(batch => batch.length), [3, 6, 12, 24, 5]);
  assert.deepEqual(batches.flat(), ids);
  assert.deepEqual(readingBatches([]), []);
});

test("what is found is shown as it comes, without waiting for the last project", async () => {
  const server = host({ a: [row("one")], d: [row("four")], z: [row("not a project of the window")] });
  const hub = createWorkItemsHub(server.api, noTimers);
  const seen: string[] = [];
  hub.subscribe(() => { const state = hub.getSnapshot(); seen.push(`${state.projects.map(project => project.projectId).join(",")}|${state.complete}`); });
  server.holding(true);
  const disconnect = hub.connect("epoch");
  await settled();
  assert.deepEqual(server.asked, [], "nothing is read before the window says which projects it has");

  hub.setProjects(["a", "b", "c", "d", "e"]);
  await settled();
  assert.deepEqual(server.asked, [["a", "b", "c"]]);
  assert.equal(hub.getSnapshot().loaded, false);
  server.release();
  await settled();
  assert.deepEqual(hub.getSnapshot().projects.map(project => project.projectId), ["a"], "the first projects are shown while the others are read");
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().complete, hub.getSnapshot().available], [true, false, true]);
  server.release();
  await settled();
  assert.deepEqual(server.asked, [["a", "b", "c"], ["d", "e"]]);
  assert.deepEqual(hub.getSnapshot().projects.map(project => project.projectId), ["a", "d"]);
  assert.equal(hub.getSnapshot().complete, true);
  assert.deepEqual(seen.slice(-3), ["a|false", "a,d|false", "a,d|true"]);

  // The same order again reads nothing; a new project is read.
  server.holding(false);
  hub.setProjects(["a", "b", "c", "d", "e"]);
  await settled();
  assert.equal(server.asked.length, 2);
  hub.setProjects(["d", "a", "b", "c", "e"]);
  await settled();
  assert.equal(server.asked.length, 2, "another order is the order of the next reading");
  server.change();
  await settled(); await settled();
  assert.deepEqual(server.asked.slice(2), [["d", "a", "b"], ["c", "e"]], "the host said something changed: everything is read again, in the new order");
  assert.deepEqual(hub.getSnapshot().projects.map(project => project.projectId), ["d", "a"]);

  disconnect();
  assert.deepEqual(hub.getSnapshot().projects, []);
});

test("a change reads its project at once and says how it ended", async () => {
  const server = host({ a: [row("one"), row("two")] });
  const hub = createWorkItemsHub(server.api, noTimers);
  hub.connect("epoch");
  hub.setProjects(["a"]);
  await settled(); await settled();

  const outcome = await hub.act({ projectId: "a", kind: "task", id: "one" }, "dismiss");
  assert.equal(outcome.ok, true);
  assert.deepEqual(server.asked.at(-1), ["a"]);
  assert.deepEqual(hub.getSnapshot().projects[0].tasks.map(item => item.id), ["two"]);
  await hub.act({ projectId: "a", kind: "task", id: "two" }, "dismiss");
  assert.deepEqual(hub.getSnapshot().projects, [], "a project that has nothing left is not listed");

  assert.deepEqual(await hub.read({ projectId: "a", kind: "task", id: "one" }), { markdown: "Text.", truncated: false });
  assert.equal(await hub.saveSettings({ ...defaultWorkSettings, start: "here" }), true);
  assert.equal(hub.getSnapshot().settings.start, "here");
});

test("a host that keeps no work items is said so", async () => {
  const hub = createWorkItemsHub(host({ a: [row("one")] }).api, noTimers);
  hub.connect("another epoch");
  hub.setProjects(["a"]);
  await settled(); await settled();
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().available, hub.getSnapshot().complete], [true, false, true]);
});

test("work starts in a new session, or through the composer of the session that shows the item", async () => {
  const server = host({ a: [row("one")] });
  const hub = createWorkItemsHub(server.api, noTimers);
  hub.connect("epoch");
  hub.setProjects(["a"]);
  await settled(); await settled();
  const item: WorkItem = workItems(hub.getSnapshot().projects, new Set())[0];
  const plan: WorkItem = { ...item, kind: "plan", id: "plan" };
  const composer: string[] = [];
  const opened: string[] = [];
  const refused: (string | null)[] = [];
  const window = (busy: boolean, takes = true): WorkStartHost => ({ hub,
    composer: (kind, sessionId, text, agent) => {
      composer.push(`${kind}:${sessionId}:${text ?? ""}:${agent ?? ""}`);
      return { kind, sessionId, text: text ?? null, handled: true, result: kind === "state" || takes, state: kind === "state" ? { sessionId, draftText: "", busy } : null };
    },
    openSession: id => opened.push(id), refuse: (message, detail) => refused.push(message ?? detail) });

  assert.equal(await startWorkItem(window(false), item, "session", { id: "s1", workingDirectory: "C:/app" }), true);
  assert.deepEqual([server.acted.at(-1), opened], ["start_session:one:s1", ["new"]], "the new session takes the model of the one that showed the item, and is shown");
  assert.deepEqual(server.ranWith.at(-1), [null, null, null], "nothing was chosen: the host takes what the session, then the item, then the defaults say");

  // What the user chose in the Work items tab goes with the start; an effort is of a model.
  assert.equal(await startWorkItem(window(false), item, "session", null, { providerId: "codex", modelId: "gpt-a", reasoningEffort: "high" }), true);
  assert.deepEqual([server.acted.at(-1), server.ranWith.at(-1)], ["start_session:one:", ["codex", "gpt-a", "high"]]);
  assert.equal(await startWorkItem(window(false), item, "session", null, { providerId: "codex", modelId: null, reasoningEffort: "high" }), true);
  assert.deepEqual(server.ranWith.at(-1), ["codex", null, null]);
  opened.splice(0, opened.length, "new");

  assert.equal(await startWorkItem(window(false), item, "here", { id: "s1", workingDirectory: "C:/app" }), true);
  assert.deepEqual(composer.splice(0), ["state:s1::", "send:s1:Do it.:"], "an idle session is sent the prompt");
  assert.equal(await startWorkItem(window(true), plan, "here", { id: "s1", workingDirectory: "C:/app" }), true);
  assert.deepEqual(composer.splice(0), ["state:s1::", "enqueue:s1:Do it.:default"], "a busy one queues it; a plan goes to the default agent");

  assert.equal(await startWorkItem(window(false, false), item, "here", { id: "s1", workingDirectory: null }), false);
  assert.equal(server.acted.at(-1), "release:one:", "a prompt the session did not take leaves nobody carrying the item out");
  assert.equal(refused.pop(), "This session cannot take it right now. Try again in a moment, or start it in a new session.");
  assert.equal(await startWorkItem(window(false), item, "here", null), false, "without a session there is nowhere to do it");

  assert.equal(await startWorkItem(window(false), item, "worktree", null), false);
  assert.equal(refused.pop(), "This project has no git repository with a commit: start it in a new session instead.");
  assert.equal(startRefusal("anything else"), null, "what the host said is shown when the window has no text for it");
});
