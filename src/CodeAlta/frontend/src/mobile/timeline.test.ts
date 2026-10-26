import assert from "node:assert/strict";
import test from "node:test";
import type { TimelineEvent } from "./contract";
import { activityFacts, appendEvents, blockVersion, foldEvents, liveBlock, pendingGrace, planTimeline, stillPending, type ActivityBlock, type PendingPrompt } from "./timeline";

let seq = 0;
const at = (second: number) => new Date(Date.UTC(2026, 9, 24, 12, 0, second)).toISOString();
const event = (kind: string, text: string | null = null, activityId: string | null = null, second = seq): TimelineEvent => ({ seq: ++seq, at: at(second), kind, text, activityId });

test("prompts, answers and errors stand alone; the work between them is one activity; turns and notices are not rows", () => {
  seq = 0;
  const events = [
    event("user.message", "Fix it"),
    event("activity.turn.started", "turn"),
    event("assistant.reasoning", "**Reading the code**"),
    event("activity.tool.started", "read_file", "c1"),
    event("activity.tool.completed", "read_file", "c1"),
    event("host.notice", "compacted"),
    event("assistant.reasoningSummary", "Then I edit it."),
    event("activity.tool.started", "apply_patch", "c2"),
    event("assistant.message", "Done."),
    event("activity.turn.completed", "turn"),
    event("error", "Rate limited"),
  ];
  const blocks = foldEvents(events);
  assert.deepEqual(blocks.map(b => b.kind === "message" ? `${b.role}:${b.text}` : `activity:${b.rows.map(r => r.type === "tool" ? `${r.title}/${r.phase}` : "thought").join(",")}`),
    ["user:Fix it", "activity:thought,read_file/completed,thought,apply_patch/started", "assistant:Done.", "error:Rate limited"]);
});

test("a block keeps its key while events are added after it", () => {
  seq = 0;
  const first = [event("user.message", "Go"), event("activity.tool.started", "shell", "c1")];
  const later = appendEvents(first, [event("activity.tool.completed", "shell", "c1"), event("assistant.message", "Ok")]);
  const before = foldEvents(first), after = foldEvents(later);
  assert.deepEqual(before.map(b => b.key), after.slice(0, 2).map(b => b.key));
  assert.equal((after[1] as ActivityBlock).rows.length, 1, "a tool is one row through its phases");
});

test("events already held are not added twice", () => {
  seq = 0;
  const held = [event("user.message", "a"), event("assistant.message", "b")];
  const again = appendEvents(held, [...held, event("user.message", "c")]);
  assert.deepEqual(again.map(e => e.seq), [1, 2, 3]);
});

test("the line of an activity counts its thoughts and tools, and a tool left running in a finished turn failed", () => {
  seq = 0;
  const [block] = foldEvents([
    event("assistant.reasoning", "I look at the tests first. Then I run them.", null, 0),
    event("activity.tool.started", "npm test", "c1", 1),
    event("activity.tool.completed", "npm test", "c1", 5),
    event("activity.command.started", "dotnet build", "c2", 6),
  ]) as ActivityBlock[];
  const running = activityFacts(block, true);
  assert.equal(running.state, "running");
  assert.equal(running.headline, "Then I run them.");
  assert.deepEqual([running.thoughts, running.tools, running.succeeded, running.failed, running.running], [1, 2, 1, 0, 1]);
  assert.equal(running.startedAt, at(0));
  assert.equal(running.endedAt, at(6));
  const over = activityFacts(block, false);
  assert.equal(over.state, "failed");
  assert.deepEqual([over.failed, over.running], [1, 0]);
});

test("an activity without a thought says what it did, with the names of its tools", () => {
  seq = 0;
  const [block] = foldEvents([event("activity.tool.started", "grep", "1"), event("activity.tool.completed", "grep", "1"), event("activity.tool.started", "read", "2"), event("activity.tool.completed", "read", "2")]) as ActivityBlock[];
  assert.deepEqual([activityFacts(block, false).headline, activityFacts(block, false).names], ["Called tools", "grep, read"]);
  assert.equal(activityFacts(block, true).headline, "Calling tools…");
});

test("only the last activity of a working session is the running one", () => {
  seq = 0;
  const blocks = foldEvents([event("user.message", "a"), event("activity.tool.started", "x", "1")]);
  assert.equal(liveBlock(blocks, 1, true), true);
  assert.equal(liveBlock(blocks, 1, false), false);
  assert.equal(liveBlock(blocks, 0, true), false);
});

test("drawing the timeline again keeps the blocks that did not change, updates the running one and adds the new ones", () => {
  seq = 0;
  const events = [event("user.message", "a"), event("activity.tool.started", "x", "1")];
  const version = (list: TimelineEvent[], busy: boolean, open = new Set<string>()) => {
    const blocks = foldEvents(list);
    return blocks.map((block, index) => ({ key: block.key, version: blockVersion(block, liveBlock(blocks, index, busy), open.has(block.key)) }));
  };
  const first = version(events, true);
  const drawn = new Map(first.map(b => [b.key, b.version]));
  const next = version(appendEvents(events, [event("activity.tool.completed", "x", "1"), event("assistant.message", "done")]), false);
  const plan = planTimeline(drawn, next);
  assert.deepEqual(plan.steps.map(s => s.action), ["keep", "update", "create"]);
  assert.deepEqual(plan.removed, []);
  // Opening an activity changes it alone.
  const opened = version(events, true, new Set([first[1].key]));
  assert.deepEqual(planTimeline(drawn, opened).steps.map(s => s.action), ["keep", "update"]);
  // Blocks that are gone (another read replaced the events) are removed.
  assert.deepEqual(planTimeline(drawn, []).removed, first.map(b => b.key));
});

test("a user message keeps its words and the file names the server sent", () => {
  seq = 0;
  const sent = { ...event("user.message", "Read these"), attachments: ["photo.png", "notes.txt"] };
  const [message] = foldEvents([sent]);
  assert.equal(message.kind, "message");
  if (message.kind !== "message") return;
  assert.equal(message.text, "Read these");
  assert.deepEqual(message.attachments, ["photo.png", "notes.txt"]);
  const prompt: PendingPrompt = { key: "p", text: "Read these", mode: "send", sentAt: 0, after: 0, attachments: ["photo.png", "notes.txt"] };
  assert.deepEqual(stillPending([prompt], [sent], true, pendingGrace).map(item => item.key), []);
});
