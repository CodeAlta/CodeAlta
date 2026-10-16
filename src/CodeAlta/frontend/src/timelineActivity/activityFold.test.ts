import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse } from "#neoastra";
import { reconcileTimeline } from "../reconcileTimeline";
import { groupTimelineTools } from "../toolGroups";
import { groupEntryTypes } from "../timelineEmphasis/timelineEmphasis";
import { activityDuration, activityExpanded, activitySummary, foldTimeline, stepName, thoughtHeadline, type TimelineBlock } from "./activityFold";

type Entry = HistoryResponse["entries"][number];
const entry = (offset: string, overrides: Partial<Entry> = {}): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: `2026-10-16T00:00:${offset.padStart(2, "0")}Z`, kind: "Assistant", phase: null,
  contentId: `content-${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null,
  tool: null, files: null, images: null, text: `message ${offset}`, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
const prompt = (offset: string, text = "Do it") => entry(offset, { kind: "User", text });
const thought = (offset: string, text: string) => entry(offset, { kind: "Reasoning", text });
const call = (offset: string, id: string, phase = "Completed", command = "dotnet build") => entry(offset, { eventType: "activity", kind: "ToolCall", activityId: id,
  contentId: null, name: "Bash", phase, text: null, tool: { primary: command, isCommand: true, output: null, outputLines: 0, outputBytes: null, exitCode: null, added: null, removed: null, fields: [] } as unknown as Entry["tool"] });
const status = (offset: string) => entry(offset, { eventType: "sessionUpdate", kind: "ModelChanged", contentId: null, text: "Model changed", details: '{"modelId":"opus"}' });

function fold(entries: Entry[]): TimelineBlock[] {
  const rows = reconcileTimeline(entries, null);
  const groups = groupTimelineTools(rows, entries);
  return foldTimeline(groups, groupEntryTypes(groups));
}

const shape = (blocks: TimelineBlock[]) => blocks.map(block => block.kind === "row" ? "row" : `activity(${block.groups.reduce((count, group) => count + group.rows.length, 0)})`);

test("the work between two messages folds into one activity, the messages keep their own line", () => {
  const blocks = fold([prompt("1"), status("2"), thought("3", "**Reading the logs**\n\nI look at them."), call("4", "a"), call("5", "b"),
    thought("6", "Now the fix"), call("7", "c"), entry("8", { text: "Done." }), prompt("9"), entry("10", { text: "Again." })]);
  assert.deepEqual(shape(blocks), ["row", "activity(6)", "row", "row", "row"]);
  assert.equal(blocks[1].key, `activity:${blocks[1].kind === "activity" ? blocks[1].groups[0].key : ""}`, "an activity is named by its first group, so it keeps its key while it grows");
});

test("a lone one-line row between two messages is not folded, a lone call or thought is", () => {
  assert.deepEqual(shape(fold([prompt("1"), status("2"), entry("3")])), ["row", "row", "row"]);
  assert.deepEqual(shape(fold([prompt("1"), call("2", "a"), entry("3")])), ["row", "activity(1)", "row"]);
  assert.deepEqual(shape(fold([prompt("1"), thought("2", "Hmm"), entry("3")])), ["row", "activity(1)", "row"]);
});

test("errors and background jobs: what needs the user stands alone, a finished job is part of the work", () => {
  const job = entry("3", { kind: "User", text: "[CodeAlta background job]\nJob: job-1\nTitle: Build\nCommand: dotnet build\nResult: succeeded (exit code 0) after 3 s\n\nOutput:\n```\nok\n```" });
  const blocks = fold([prompt("1"), entry("2", { text: "Started." }), job, call("4", "a"), entry("5", { eventType: "error", kind: "error", contentId: null, text: "Boom" }), call("6", "b")]);
  assert.deepEqual(shape(blocks), ["row", "row", "activity(2)", "row", "activity(1)"]);
  const activity = blocks[2];
  assert.equal(activity.kind, "activity");
  if (activity.kind !== "activity") return;
  const summary = activitySummary(activity.groups);
  assert.deepEqual([summary.jobs, summary.tools, summary.succeeded], [1, 1, 2]);
  assert.equal(summary.latest[0].title, "Build", "a job is named by its title");
});

test("the summary counts the steps, keeps the newest thought as headline and the last steps as progress", () => {
  const blocks = fold([prompt("1"), thought("2", "First idea"), call("3", "a"), call("4", "b", "Failed", "npm test"),
    thought("5", "**Checking the release**\nmore"), call("6", "c", "Started", "gh run view 42"), call("7", "d", "Completed", "C:\\tools\\wc.exe -l"), call("8", "e", "Requested", "ls")]);
  const activity = blocks[1];
  assert.equal(activity.kind, "activity");
  if (activity.kind !== "activity") return;
  const summary = activitySummary(activity.groups);
  assert.equal(summary.headline, "Checking the release");
  assert.deepEqual([summary.thoughts, summary.tools, summary.succeeded, summary.failed, summary.running], [2, 5, 2, 1, 2]);
  assert.deepEqual(summary.latest.map(step => step.state), ["running", "completed", "pending"]);
  assert.deepEqual(summary.names, ["Bash dotnet", "Bash npm", "Bash gh"], "the first different steps, by tool and program");
  assert.equal(summary.endedAt - summary.startedAt, 6000);
});

test("without a thought, the headline is left to the line, which says that tools are called", () => {
  const blocks = fold([prompt("1"), call("2", "a"), call("3", "b", "Completed", "wc -l x")]);
  const activity = blocks[1];
  if (activity.kind !== "activity") return assert.fail("an activity");
  const summary = activitySummary(activity.groups);
  assert.equal(summary.headline, null);
  assert.deepEqual(summary.names, ["Bash dotnet", "Bash wc"]);
});

test("short names, thoughts and durations", () => {
  assert.equal(stepName({ title: "write_file", detail: "src/a.ts", detailIsCode: false }), "write_file");
  assert.equal(stepName({ title: "Bash", detail: "\"C:/Program Files/git\" status", detailIsCode: true }), "Bash git");
  assert.equal(stepName({ title: "Read", detail: "C:\\code\\main.tsx", detailIsCode: true }), "Read main.tsx");
  assert.equal(stepName({ title: "alta", detail: "alta skill activate ilspy-decompile", detailIsCode: true }), "alta skill", "a command that repeats its tool");
  assert.equal(stepName({ title: "alta", detail: "alta", detailIsCode: true }), "alta");
  assert.equal(stepName({ title: "shell_command", detail: "(Get-Content a.ts).Count", detailIsCode: true }), "shell_command Get-Content");
  assert.equal(stepName({ title: "shell_command", detail: "& git status", detailIsCode: true }), "shell_command git");
  assert.equal(thoughtHeadline("**Plan**\n\nthen more"), "Plan", "a reasoning summary is named by its title");
  assert.equal(thoughtHeadline("I see the files. Now I count the lines, so I run one command."), "Now I count the lines, so I run one command.",
    "a thought in prose says last what it is about to do");
  assert.equal(thoughtHeadline("Older idea.\n\nI check the log. Done."), "I check the log. Done.", "a last sentence too short keeps the one before");
  assert.equal(thoughtHeadline("I run it:\n\n```\nls -la\n```"), "I run it:", "code is not a sentence");
  assert.equal(thoughtHeadline("Use `git` and **care**"), "Use git and care");
  assert.equal(thoughtHeadline("   "), null);
  assert.deepEqual([activityDuration(4400), activityDuration(133_000), activityDuration(3_840_000), activityDuration(NaN)], ["4s", "2m 13s", "1h 4m", null]);
});

test("an activity is open by the reader's choice, else by the mode, and opens on a message it is asked to show", () => {
  const blocks = fold([prompt("1"), thought("2", "Hmm"), call("3", "a")]);
  const activity = blocks[1];
  if (activity.kind !== "activity") return assert.fail("an activity");
  const keys = activity.groups.flatMap(group => group.rows.map(row => row.key));
  assert.equal(activityExpanded(activity.groups, new Map(), true), false);
  assert.equal(activityExpanded(activity.groups, new Map(), false), true);
  assert.equal(activityExpanded(activity.groups, new Map([[keys[1], true]]), true), true, "a choice on any of its rows holds while it grows");
  assert.equal(activityExpanded(activity.groups, new Map([[keys[0], false]]), false), false);
  assert.equal(activityExpanded(activity.groups, new Map(), true, "2"), true);
});
