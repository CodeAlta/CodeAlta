import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SessionRuntimeBackgroundTaskResponse, SessionRuntimeScopedResponse, SessionRuntimeStateEntry } from "#neoastra";
import { BackgroundCallsContext, BackgroundMark, BackgroundTasksStatus } from "./BackgroundTaskViews";
import { backgroundCalls, backgroundTaskElapsed, backgroundTaskIcon, backgroundTaskKind, backgroundTasks, runningBackgroundTasks, sameBackgroundTasks } from "./backgroundTasks";
import { createRuntimeObservations, projectRuntimeObservation, sessionBackground, sessionRunning, type RuntimeTarget } from "./runtimeObservations";
import { TimelineMessage } from "./TimelineMessage";
import type { TimelineItem } from "./timeline";

const task = (taskId: string, state = "running", toolCallId: string | null = null): SessionRuntimeBackgroundTaskResponse =>
  ({ taskId, kind: "command", description: `Does ${taskId}`, toolCallId, startedAt: state === "running" ? "2026-01-01T00:00:00Z" : null, state });
const entry = (tasks: unknown, changes: Partial<SessionRuntimeStateEntry> = {}): SessionRuntimeStateEntry => ({ attachmentGeneration: "9", isTerminated: false, isRetiring: false,
  activeRunId: null, backgroundTasks: tasks as SessionRuntimeBackgroundTaskResponse[], queueDrainInProgress: false, providerId: "fake", providerKey: "fake", modelId: null,
  reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null, ...changes });

test("the background tasks of a session are read as the host lists them, and what is not a task is left out", () => {
  const read = backgroundTasks(entry([task("b1", "running", "toolu_1"), task("b2", "failed", "toolu_2"), task("b3", "stopped")]));
  assert.deepEqual(read.map(item => [item.id, item.state, item.toolCallId, item.startedAt]),
    [["b1", "running", "toolu_1", "2026-01-01T00:00:00Z"], ["b2", "failed", "toolu_2", null], ["b3", "stopped", null, null]]);
  assert.deepEqual(read[0], { id: "b1", kind: "command", description: "Does b1", toolCallId: "toolu_1", startedAt: "2026-01-01T00:00:00Z", state: "running" });
  // A state this version does not know, a twice listed task, a task without an identity or a kind, and what is not a task.
  assert.deepEqual(backgroundTasks(entry([task("b1"), task("b1"), task("b2", "paused"), { ...task("b3"), taskId: "" }, { ...task("b4"), kind: 7 }, null, "b5",
    { ...task("b6"), description: 4, toolCallId: "", startedAt: "not a time" }])).map(item => [item.id, item.description, item.toolCallId, item.startedAt]),
    [["b1", "Does b1", null, "2026-01-01T00:00:00Z"], ["b6", null, null, null]]);
  // No more than the page shows, and none for an attachment that is ending or that reports none.
  assert.equal(backgroundTasks(entry(Array.from({ length: 40 }, (_, index) => task(`t${index}`)))).length, 16);
  for (const without of [null, undefined, entry([]), entry("tasks"), entry([task("b1")], { isRetiring: true }), entry([task("b1")], { isTerminated: true })])
    assert.equal(backgroundTasks(without).length, 0);
  // The same empty list is given each time: a session without tasks renders nothing again for it.
  assert.equal(backgroundTasks(null), backgroundTasks(entry([])));

  assert.deepEqual(runningBackgroundTasks(read).map(item => item.id), ["b1"]);
  assert.equal(runningBackgroundTasks(read.slice(0, 1)).length, 1);
  assert.equal(runningBackgroundTasks(read.slice(1)), backgroundTasks(null));
  assert.equal(sameBackgroundTasks(read, backgroundTasks(entry([task("b1", "running", "toolu_1"), task("b2", "failed", "toolu_2"), task("b3", "stopped")]))), true);
  assert.equal(sameBackgroundTasks(read, read.slice(0, 2)), false);
  assert.equal(sameBackgroundTasks(read, backgroundTasks(entry([task("b1", "running", "toolu_1"), task("b2", "stopped", "toolu_2"), task("b3", "stopped")]))), false);
});

test("a tool call is shown with what its task does in the background, and a task that runs again wins over its previous end", () => {
  const calls = backgroundCalls(backgroundTasks(entry([task("b1", "running", "toolu_1"), task("b2", "failed", "toolu_2"), task("b3", "stopped"),
    task("b4", "stopped", "toolu_1"), task("b5", "stopped", "toolu_5"), task("b6", "running", "toolu_5")])));
  assert.deepEqual([...calls], [["toolu_1", "running"], ["toolu_2", "failed"], ["toolu_5", "running"]]);
  assert.deepEqual(["command", "agent", "workflow", "watch"].map(backgroundTaskKind), ["Command", "Agent", "Workflow", "Task"]);
  assert.deepEqual(["command", "agent", "watch"].map(backgroundTaskIcon), ["terminal", "assistant", "task"]);
  const now = Date.parse("2026-01-01T00:02:05Z");
  assert.equal(backgroundTaskElapsed("2026-01-01T00:00:00Z", now), "2m 5s");
  assert.equal(backgroundTaskElapsed("2026-01-01T00:02:00Z", now), "5s");
  assert.equal(backgroundTaskElapsed(null, now), null);
  assert.equal(backgroundTaskElapsed("not a time", now), null);
});

test("a session whose provider works in the background is marked without being shown as running", async () => {
  const target: RuntimeTarget = { tab: { sessionId: "one", projectId: null, path: null },
    request: { expectedHostEpoch: "epoch", sessionId: "one", createdAt: "2026-01-01T00:00:00Z", scope: "global", projectId: null, projectPath: null } };
  const reply = (tasks: SessionRuntimeBackgroundTaskResponse[], changes: Partial<SessionRuntimeStateEntry> = {}): SessionRuntimeScopedResponse => ({ status: "ok", hostEpoch: "epoch",
    sessionId: "one", scope: "global", projectId: null, projectPath: null,
    observation: { status: "ok", hostEpoch: "epoch", sessionId: "one", runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false, entry: entry(tasks, changes) } });
  // Only the tasks that go on are counted, and an attachment that is ending has none.
  assert.equal(projectRuntimeObservation(reply([task("b1"), task("b2"), task("b3", "failed")])).background, 2);
  assert.equal(projectRuntimeObservation(reply([task("b1")])).running, false);
  assert.equal(projectRuntimeObservation(reply([task("b1")], { isRetiring: true })).background, 0);

  let answer = reply([task("b1"), task("b2")]);
  const store = createRuntimeObservations(async () => answer);
  await store.refresh([target]);
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 2);
  assert.equal(sessionRunning(store.getSnapshot(), target.tab), false);
  assert.equal(store.getRunning().size, 0, "A session that only works in the background is not one that runs.");
  // What the open panel sees wins over what was polled, for the tasks as for the run.
  let published = 0;
  store.subscribe(() => { published++; });
  store.setLive(target.tab, false, 1);
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 1);
  store.setLive(target.tab, false, 1);
  assert.equal(published, 1, "A report that changes nothing is not published.");
  store.setLive(target.tab, true, 1);
  assert.equal(sessionRunning(store.getSnapshot(), target.tab), true);
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 1);
  store.setLive(target.tab, false);
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 0);
  // The panel stops watching: the polled row is what is known again, and it keeps its count while it reloads.
  store.setLive(target.tab, null);
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 2);
  answer = reply([]);
  const reading = store.refresh([target]);
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 2);
  await reading;
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 0);
  // What is stale is not shown.
  answer = reply([task("b1")]);
  await store.refresh([target]);
  store.invalidate();
  assert.equal(sessionBackground(store.getSnapshot(), target.tab), 0);
});

test("the composer says how many tasks go on, and the mark of a session says the same", () => {
  const one = backgroundTasks(entry([task("b1")]));
  const never = () => assert.fail("rendering must not act");
  assert.match(renderToStaticMarkup(<BackgroundMark count={1} />), /^<span class="session-background" role="img" aria-label="1 background task" title="1 background task"><\/span>$/);
  assert.match(renderToStaticMarkup(<BackgroundMark count={3} />), /aria-label="3 background tasks"/);
  assert.match(renderToStaticMarkup(<BackgroundTasksStatus tasks={one} onStop={never} />), /<button[^>]*background-tasks-status[^>]*>.*1 background task</s);
  assert.match(renderToStaticMarkup(<BackgroundTasksStatus tasks={backgroundTasks(entry([task("b1"), task("b2")]))} onStop={never} />), />2 background tasks</);
  // Nothing goes on: the composer says nothing of it.
  assert.equal(renderToStaticMarkup(<BackgroundTasksStatus tasks={[]} onStop={never} />), "");
});

test("the tile of a call that started a task says what the task does, beside the state of the call", () => {
  const item: TimelineItem = { key: "42", eventType: "activity", category: "tool", icon: "terminal", title: "Bash", subtitle: null, timestamp: "2026-09-27T10:00:00Z",
    markdown: null, summary: "sleep 600", summaryIsCode: true, detailMarkdown: null, details: "{}", detailsLabel: "Details", metadata: [], truncated: false, bodyOmitted: false,
    copyMarkdown: null, toolPhase: "completed",
    toolCall: { providerId: "fake", runId: "run", activityId: "toolu_1", kind: "ToolCall", name: "Bash", offset: "1", outputOffset: null, startedAt: null, endedAt: null } };
  const draw = (calls: ReadonlyMap<string, "running" | "failed" | "stopped">, changes: Partial<TimelineItem> = {}) =>
    renderToStaticMarkup(<BackgroundCallsContext.Provider value={calls}><TimelineMessage toolTile item={{ ...item, ...changes }} /></BackgroundCallsContext.Provider>);
  // Without a task, the call says that it completed.
  const plain = draw(new Map());
  assert.match(plain, /tool-outcome[^>]*>Completed</);
  assert.doesNotMatch(plain, /data-tool-background/);
  // Its task goes on: the tile works, and says where.
  const running = draw(new Map([["toolu_1", "running"]]));
  assert.match(running, /data-tool-phase="completed" data-tool-background="running"/);
  assert.match(running, /tool-outcome[^>]*>Running in the background</);
  assert.match(running, /tool-state-spinner/);
  assert.doesNotMatch(running, /tool-state-dot/);
  // Its task ended without running to its end.
  assert.match(draw(new Map([["toolu_1", "failed"]])), /data-tool-background="failed".*tool-outcome[^>]*>Failed in the background</s);
  const stopped = draw(new Map([["toolu_1", "stopped"]]));
  assert.match(stopped, /tool-outcome[^>]*>Stopped in the background</);
  assert.match(stopped, /tool-state-dot/);
  // The task of another call changes nothing, and neither does a task for a call that still runs or failed by itself.
  assert.match(draw(new Map([["toolu_2", "running"]])), /tool-outcome[^>]*>Completed</);
  assert.match(draw(new Map([["toolu_1", "running"]]), { toolPhase: "failed" }), /tool-outcome[^>]*>Failed</);
  assert.doesNotMatch(draw(new Map([["toolu_1", "running"]]), { toolPhase: "started" }), /data-tool-background/);
});
