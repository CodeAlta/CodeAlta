import assert from "node:assert/strict";
import test from "node:test";
import { buildTimelineItems, scriptRunOf, type HistoryEntry } from "../timeline";
import { hasWorkflowBlock } from "./runFlow";
import { parseScriptResult, parseWorkflowResult, plainResult, presentedHeading } from "./scriptResult";

// What the card of a run reads of what its script presented (`present()`); presented.browser.test.ts looks at the cards.

const entry = (offset: string, changes: Partial<HistoryEntry> = {}): HistoryEntry => ({ offset,
  eventType: "activity", kind: "ToolCall", providerId: "provider", runId: "run", sessionId: "session",
  activityId: null, parentActivityId: null, phase: null, contentId: null, interactionId: null, sourceSessionId: null, prompt: null,
  timestamp: "2026-10-29T10:00:00Z", name: null, text: null, details: null,
  tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...changes });
const exec = (offset: string, phase: string) => entry(offset, { activityId: "call-1", name: "exec", phase,
  details: JSON.stringify({ arguments: { code: '// @exec: {"title": "Review PR 231"}\nreturn 1;' } }) });
const record = (offset: string, kind: string, changes: Partial<HistoryEntry> = {}, details: object = {}) => entry(offset, { eventType: "workflow", kind,
  parentActivityId: "call-1", name: "run-1", details: JSON.stringify({ kind, ...details }), ...changes });

// A run of an agent that presents twice, the second time in a step, and returns data.
const records = () => [
  record("2", "RunStarted", { text: "Review PR 231" }),
  record("3", "Presented", { text: "## Reading\n- 3 files" }),
  record("4", "StepStarted", { activityId: "review" }),
  record("5", "Presented", { activityId: "review", text: "## Findings\n- **one** issue", textTruncated: true }),
];
const result = "Script run-1 (Review PR 231) ended:\nScript completed · 4.0 s · 2 calls\n"
  + `${presentedHeading}\n\`\`\`markdown\n## Reading\n- 3 files\n\n## Findings\n- **one** issue\n\`\`\`\nResult:\n\`\`\`json\n{"issues":1,"files":["a.cs"]}\n\`\`\`\n`;

test("the parts a run presented come from its records, in their order, as they come and after a reload", () => {
  const live = buildTimelineItems([exec("1", "Started"), ...records().slice(0, 2)])[0].scriptRun!;
  assert.deepEqual(live.presented?.map(part => part.markdown), ["## Reading\n- 3 files"], "the first part, live");
  assert.ok(hasWorkflowBlock(live), "a run that presented has its block outside the folded calls");

  const later = buildTimelineItems([exec("1", "Started"), ...records()])[0].scriptRun!;
  assert.deepEqual(later.presented, [
    { key: "3", markdown: "## Reading\n- 3 files", stepId: null, offset: "3", shortened: false },
    { key: "5", markdown: "## Findings\n- **one** issue", stepId: "review", offset: "5", shortened: true },
  ]);
  // A reload reads new objects of the same records: the same parts.
  const reloaded = buildTimelineItems(JSON.parse(JSON.stringify([exec("1", "Completed"), ...records()])))[0].scriptRun!;
  assert.deepEqual(reloaded.presented, later.presented);
  assert.equal(later.forUser, undefined, "a run of an agent");
  assert.equal(scriptRunOf([{ kind: "RunStarted", details: JSON.stringify({ forUser: true }) }], "run-2").forUser, true);
  assert.equal(hasWorkflowBlock(scriptRunOf([{ kind: "RunStarted" }, { kind: "RunEnded", phase: "completed" }])), false, "nor a run that did nothing to show");
});

test("the result of a run of an agent has no body: what it presented, and its value as plain text", () => {
  const read = parseWorkflowResult(result)!;
  assert.equal(read.presented, "## Reading\n- 3 files\n\n## Findings\n- **one** issue");
  assert.equal(read.notes, "", "the parts are not what the run wrote");
  assert.equal(plainResult(read.result!), '{\n  "issues": 1,\n  "files": [\n    "a.cs"\n  ]\n}');

  // Without the records of its run in the window: the parts its session was told.
  const [card] = buildTimelineItems([entry("9", { eventType: "contentCompleted", kind: "User", text: result, provenance: "workflow" })]);
  assert.equal(card.markdown, null, "the value is no body");
  assert.equal(card.workflowRun, undefined);
  assert.equal(card.copyMarkdown, `${read.presented}\n\n${read.result}`);

  // With them: the parts of the records, read whole where the page shortened one.
  const [withRun] = buildTimelineItems([exec("1", "Completed"), ...records(), entry("9", { eventType: "contentCompleted", kind: "User", text: result, provenance: "workflow" })])
    .filter(item => item.workflowResult);
  assert.equal(withRun.workflowRun?.presented?.length, 2);
});

test("the result of a script the user ran from the library keeps its value as Markdown", () => {
  const library = "Script run-1 (review) ended:\nScript completed · 4.0 s · 2 calls\nResult:\n## Review\n- **ok**\n";
  const [card] = buildTimelineItems([record("2", "RunStarted", { text: "review" }, { forUser: true }),
    entry("9", { eventType: "contentCompleted", kind: "User", text: library, provenance: "workflow" })]);
  assert.equal(card.markdown, "## Review\n- **ok**");
  assert.equal(card.workflowRun?.forUser, true);
});

test("a line Result: in what a run presented is not its result", () => {
  const read = parseScriptResult(`Script completed · 1 ms · no calls\n${presentedHeading}\n\`\`\`\`markdown\n\`\`\`text\nResult:\n\`\`\`\n\`\`\`\`\nResult:\n42\n`)!;
  assert.equal(read.presented, "```text\nResult:\n```");
  assert.equal(read.result, "42");
});
