import assert from "node:assert/strict";
import test from "node:test";
import type { TimelineEvent } from "./contract";
import {
  activityFacts, appendEvents, blockTime, blockVersion, canWithdraw, clockTime, dayLabel, foldEvents, liveBlock, pendingBlock, pendingGrace, planTimeline, prependEvents, stillPending, subjectPath, withDays, withQueue,
  type ActivityBlock, type DayBlock, type MessageBlock, type PendingPrompt, type TimelineBlock,
} from "./timeline";

let seq = 0;
const at = (second: number) => new Date(Date.UTC(2026, 9, 24, 12, 0, second)).toISOString();
const event = (kind: string, text: string | null = null, activityId: string | null = null, second = seq): TimelineEvent => ({ seq: ++seq, at: at(second), kind, text, activityId });

test("a subject that is a path comes apart before the name of its file; anything else stays whole", () => {
  assert.deepEqual(subjectPath("C:/dev2/stride/Upgrade/Shader.cs"), { folders: "C:/dev2/stride/Upgrade", file: "/Shader.cs" });
  assert.deepEqual(subjectPath("C:\\Users\\me\\Stride Projects\\a b.cs"), { folders: "C:\\Users\\me\\Stride Projects", file: "\\a b.cs" });
  assert.deepEqual(subjectPath("src/app.ts"), { folders: "src", file: "/app.ts" });
  for (const whole of ["dotnet build src/a.csproj -c Release", "a.txt", "login", "src/", "/root"]) assert.equal(subjectPath(whole), null, whole);
});

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
  assert.deepEqual(blocks.map(b => b.kind === "message" ? `${b.role}:${b.text}` : b.kind === "workflow" ? "workflow" : `activity:${b.rows.map(r => r.type === "tool" ? `${r.title}/${r.phase}` : "thought").join(",")}`),
    ["user:Fix it", "activity:thought,read_file/completed,thought,apply_patch/started", "assistant:Done.", "error:Rate limited"]);
});

test("an older page goes before the events held, without the ones held already, and folds with them", () => {
  const message = (number: number, kind = "assistant.message", text: string | null = `m${number}`, activityId: string | null = null): TimelineEvent =>
    ({ seq: number, at: at(number), kind, text, activityId });
  const held = [message(3, "activity.tool.completed", "read_file", "c1"), message(4)];
  const merged = prependEvents(held, [message(2, "activity.tool.started", "read_file", "c1"), message(3, "activity.tool.completed", "read_file", "c1"), message(1, "user.message")]);
  assert.deepEqual(merged.map(event => event.seq), [1, 2, 3, 4]);
  assert.deepEqual(prependEvents([], [message(1)]).map(event => event.seq), [1]);
  // A page that started inside an activity: the same call, one row, once the older page is there.
  const [, activity] = foldEvents(merged);
  assert.deepEqual((activity as ActivityBlock).rows.map(row => row.type === "tool" ? [row.key, row.phase] : null), [["cc1", "completed"]]);
});

test("a message keeps its time, and a block says when it started", () => {
  const blocks = foldEvents([
    { seq: 1, at: at(1), kind: "user.message", text: "Go", activityId: null },
    { seq: 2, at: at(2), kind: "assistant.reasoning", text: "Thinking", activityId: null },
    { seq: 3, at: null, kind: "assistant.message", text: "No time", activityId: null },
  ]);
  assert.deepEqual(blocks.map(blockTime), [at(1), at(2), null]);
  assert.equal(blockTime(pendingBlock({ key: "p1", text: "x", mode: "send", sentAt: Date.UTC(2026, 9, 24, 12), after: 0 })), new Date(Date.UTC(2026, 9, 24, 12)).toISOString());
  assert.equal(clockTime(null), null);
  assert.equal(clockTime("not a date"), null);
  assert.match(clockTime(at(1))!, /\d{1,2}.\d{2}/u);
});

test("a day is Today, Yesterday, or its date, with its year when it is not this one", () => {
  const now = new Date(2026, 10, 2, 0, 30);
  assert.equal(dayLabel(new Date(2026, 10, 2, 23, 59), now), "Today");
  assert.equal(dayLabel(new Date(2026, 10, 1, 0, 0), now), "Yesterday");
  const earlier = dayLabel(new Date(2026, 9, 26, 12), now);
  assert.ok(!/Today|Yesterday|2026/u.test(earlier), earlier);
  assert.match(earlier, /26/u);
  assert.match(dayLabel(new Date(2025, 11, 31, 12), now), /2025/u);
  // The day a clock change makes 23 hours long is still one day.
  assert.equal(dayLabel(new Date(2026, 2, 28, 12), new Date(2026, 2, 29, 23, 59)), "Yesterday");
});

test("the line of a day comes before the first block of that day, once, in the time of the phone", () => {
  const message = (number: number, when: Date | null): MessageBlock =>
    ({ kind: "message", key: `m${number}`, role: "user", text: "x", seq: number, at: when?.toISOString() ?? null });
  const now = new Date(2026, 10, 2, 12);
  const shown = (blocks: readonly MessageBlock[]) => withDays<TimelineBlock>(blocks, now).map(block => block.kind === "day" ? (block as DayBlock).label : block.key);
  const before = new Date(2026, 10, 1, 23, 59), today = new Date(2026, 10, 2, 0, 1);
  assert.deepEqual(shown([message(1, before), message(2, null), message(3, before), message(4, today), message(5, today)]),
    ["Yesterday", "m1", "m2", "m3", "Today", "m4", "m5"]);
  assert.deepEqual(shown([message(1, null), message(2, today)]), ["m1", "Today", "m2"], "A block without a time needs no line.");
  assert.deepEqual(shown([message(1, today), message(2, before), message(3, today)]), ["Today", "m1", "Yesterday", "m2", "m3"], "A day has one line: its key is unique on the page.");
  assert.deepEqual(shown([]), []);
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

test("a user message keeps its words and the files the server named: their titles, types and ids", () => {
  seq = 0;
  const files = [{ title: "photo.png", mediaType: "image/png", id: "20261024-01-photo-1a2b.png" }, { title: "notes.txt", mediaType: "text/plain", id: "20261024-02-notes-1a2b.txt" }];
  const sent = { ...event("user.message", "Read these"), attachments: [...files, { title: " ", mediaType: "text/plain", id: "x" }] };
  const [message] = foldEvents([sent]);
  assert.equal(message.kind, "message");
  if (message.kind !== "message") return;
  assert.equal(message.text, "Read these");
  assert.deepEqual(message.attachments, files);
  assert.equal(foldEvents([{ ...event("user.message", ""), attachments: files }]).length, 1, "Files alone are a message.");
  const prompt: PendingPrompt = { key: "p", text: "Read these", mode: "send", sentAt: 0, after: 0, attachments: files.map(({ title, mediaType }) => ({ title, mediaType })) };
  assert.deepEqual(stillPending([prompt], [sent], true, pendingGrace).map(item => item.key), []);
});

test("a queued prompt waits as long as the queue holds it, and the queue names what the phone did not send", () => {
  const queued: PendingPrompt = { key: "p1", text: "Then the docs", mode: "queue", sentAt: 0, after: 0, queueId: "queue-1" };
  assert.deepEqual(stillPending([queued], [], false, pendingGrace * 10), [queued], "no grace: the queue says when it goes");
  const steered: PendingPrompt = { key: "p2", text: "Faster", mode: "steer", sentAt: 0, after: 0, withdrawalId: "steer-1" };
  assert.deepEqual(stillPending([steered], [], false, pendingGrace * 10), [], "a steered prompt has no list to read");
  assert.equal(canWithdraw(queued) && canWithdraw(steered), true);
  assert.equal(canWithdraw({ ...steered, taken: true }), false, "too late");
  assert.equal(canWithdraw({ key: "p3", text: "x", mode: "send", sentAt: 0, after: 0 }), false, "nothing names it yet");

  // It left the queue, sent or removed elsewhere: it goes.
  assert.deepEqual(withQueue([queued, steered], [], new Set(), 5), [steered]);
  // Another prompt of the queue comes, with its files; the steered one stays.
  const read = withQueue([steered], [{ id: "queue-9", text: "From before", at: "2026-11-05T10:00:00Z", files: [{ title: "a.txt", mediaType: "text/plain" }] }], new Set(), 5);
  assert.deepEqual(read.map(prompt => [prompt.key, prompt.mode, prompt.queueId, prompt.after, prompt.attachments?.[0]?.title]),
    [["p2", "steer", undefined, 0, undefined], ["qqueue-9", "queue", "queue-9", 5, "a.txt"]]);
  // A prompt whose answer has not come yet is the one the queue names, not one more.
  const sending: PendingPrompt = { key: "p4", text: "Sent while it worked", mode: "send", sentAt: 0, after: 0 };
  const adopted = withQueue([sending], [{ id: "queue-2", text: " Sent while it worked " }], new Set(), 5);
  assert.deepEqual(adopted.map(prompt => [prompt.key, prompt.mode, prompt.queueId]), [["p4", "queue", "queue-2"]]);
  // What this page removed stays gone, whatever a read on its way says.
  assert.deepEqual(withQueue([], [{ id: "queue-3", text: "Removed" }], new Set(["queue-3"]), 5), []);
  // Nothing changes: the prompts are the same objects, so nothing is drawn again.
  assert.equal(withQueue([queued], [{ id: "queue-1", text: "Then the docs" }], new Set(), 5)[0], queued);
});

test("a tool call says what it works on, from the first of its events that does, and the running one says it on the folded line", () => {
  seq = 0;
  const blocks = foldEvents([
    event("activity.toolcall.requested", "Bash", "call-1"),
    { ...event("activity.toolcall.started", "Bash", "call-1"), subject: "git status" },
    { ...event("activity.toolcall.started", "Read", "call-2"), subject: "a.txt" },
  ]);
  const block = blocks[0];
  assert.equal(block.kind, "activity");
  if (block.kind !== "activity") return;
  assert.deepEqual(block.rows.map(row => row.type === "tool" ? [row.subject, row.seq, row.activityId] : null), [["git status", 1, "call-1"], ["a.txt", 3, "call-2"]]);
  const facts = activityFacts(block, true);
  assert.equal(facts.current, "Read · a.txt", "the last that runs");
  assert.equal(facts.names, null, "it says more than the names of the tools");
  assert.equal(activityFacts(block, false).current, null, "a turn that ended runs nothing");
  assert.notEqual(blockVersion(block, true, false), blockVersion({ ...block, rows: block.rows.map(row => row.type === "tool" ? { ...row, subject: "other" } : row) }, true, false), "a row is drawn again when it learns its subject");
  // A tool without a subject leaves the names of the tools on the line, as before.
  const named = foldEvents([event("activity.toolcall.started", "Grep", "call-3")])[0];
  if (named.kind === "activity") assert.deepEqual([activityFacts(named, true).current, activityFacts(named, true).names], [null, "Grep"]);
});

test("the host's prompts, the images of tools, the pictures of an answer and the runs of scripts fold where they belong", () => {
  seq = 0;
  const run = (kind: string, facts: object): TimelineEvent => ({ ...event(`workflow.${kind}`), workflow: { runId: "run-1", callId: "exec-1", ...facts } });
  const events: TimelineEvent[] = [
    { ...event("user.message", "[CodeAlta background job]\nJob: job-1\nResult: succeeded (exit code 0) after 2 s"), provenance: "background-job" },
    event("activity.turn.started", "turn"),
    event("activity.toolcall.started", "view_image", "view-1"),
    { ...event("tool.output", ""), parentId: "view-1", images: [{ title: "Shot", mediaType: "image/png", id: "shot.png" }] },
    event("activity.toolcall.started", "exec", "exec-1"),
    run("runStarted", { title: "Compare" }),
    { ...event("activity.toolcall.started", "read_file", "inner-1"), parentId: "exec-1" },
    { ...event("activity.toolcall.completed", "read_file", "inner-1"), parentId: "exec-1" },
    event("activity.toolcall.completed", "exec", "exec-1"),
    { ...event("assistant.message", "See ![chart](chart.png)"), images: [{ id: "0", title: "chart", src: "chart.png" }] },
    event("activity.turn.completed", "turn"),
    // The script goes on after its turn: its records join the card of its call.
    run("runEnded", { status: "completed" }),
    { ...event("workflow.runStarted"), workflow: { runId: "run-2", callId: "gone", title: "Elsewhere" } },
  ];
  const blocks = foldEvents(events);
  assert.deepEqual(blocks.map(block => block.kind === "message" ? `${block.role}${block.host ? `:${block.host.kind}` : ""}` : block.kind), ["user:job", "activity", "assistant", "workflow"]);
  const activity = blocks[1] as ActivityBlock;
  assert.deepEqual(activity.rows.map(row => row.type === "tool" ? row.title : row.type), ["view_image", "exec"], "the calls of the script are the script's");
  assert.deepEqual(activity.images?.map(image => image.id), ["shot.png"]);
  assert.deepEqual(activity.workflows?.map(item => [item.runId, item.state]), [["run-1", "completed"]]);
  assert.deepEqual(blocks[2].kind === "message" && blocks[2].images?.map(image => image.src), ["chart.png"]);
  assert.equal(blocks[3].kind === "workflow" && blocks[3].run.title, "Elsewhere", "a run whose call is not held has a card of its own");
  const before = blockVersion(foldEvents(events.slice(0, -2))[1], false, false);
  assert.notEqual(blockVersion(activity, false, false), before, "the activity is drawn again when its script ends");
});
