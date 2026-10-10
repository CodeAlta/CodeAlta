import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

// `src/CodeAlta.Plugin.Statistics.Tests/Golden/results.json` is written by the plugin's own serializer (a C# test keeps it equal to
// what `StatisticsJson` writes). Here every sample is checked against the shape the TypeScript types of the canvas declare: the same
// properties, each of its type, the optional ones left out when null. A property one side adds or renames fails a test.

type Shape = "string" | "number" | "boolean" | "any" | Readonly<{ optional: Shape }> | Readonly<{ array: Shape }> | Readonly<{ object: Readonly<Record<string, Shape>> }>;
const optional = (shape: Shape): Shape => ({ optional: shape });
const array = (shape: Shape): Shape => ({ array: shape });
const object = (fields: Record<string, Shape>): Shape => ({ object: fields });

const coverage = object({ complete: "boolean", historyState: "string", completeFrom: optional("string") });
const query = object({ period: "string", from: "string", to: "string", frequency: "string", timeZone: "string", compareFrom: optional("string"), compareTo: optional("string"), coverage, ignoredFilters: array("string"), notes: array("string") });
const bucket = object({ index: "number", start: "string", label: "string" });
const numbers = array("number");
const cost = object({ unit: "string", total: "number" });
const keyCount = object({ key: "string", value: "number" });
const tile = object({ id: "string", unit: "string", value: "number", previous: optional("number"), change: optional("number"), spark: numbers });
const toolRow = object({ kind: "string", tool: "string", calls: "number", failures: "number", failureRate: "number", timeMs: "number", p50Ms: optional("number"), p90Ms: optional("number"), maxMs: "number", bytesIn: "number", bytesOut: "number", spark: numbers });
const session = object({ sessionId: "string", title: optional("string"), project: optional("string"), projectName: optional("string"), provider: optional("string"), model: optional("string"), parentSessionId: optional("string"),
  runs: "number", activeMs: "number", tokens: "number", toolCalls: "number", costs: array(cost), subAgents: "number", lastActivity: optional("string"), deleted: "boolean" });
const modelRow = object({ provider: "string", model: "string", requests: "number", inputTokens: "number", freshInputTokens: "number", cacheReadTokens: "number", cacheWriteTokens: "number", outputTokens: "number",
  reasoningTokens: "number", cacheShare: "number", activeMs: "number", averageContextFill: optional("number"), highestContextFill: "number", costs: array(cost), spark: numbers });

const shapes: Readonly<Record<string, Shape>> = {
  SeriesResult: object({ query, metric: "string", unit: "string", group: optional("string"), buckets: array(bucket),
    series: array(object({ key: "string", label: "string", values: numbers, previous: optional(numbers), total: "number", previousTotal: optional("number") })) }),
  SummaryResult: object({ query, buckets: array(bucket), tiles: array(tile), costs: array(tile) }),
  TopResult: object({ query, kind: "string", by: "string", unit: "string", buckets: array(bucket), totalRows: "number", truncated: "boolean",
    rows: array(object({ key: "string", label: "string", detail: optional("string"), value: "number", share: "number", tokens: "number", timeMs: "number", calls: "number", requests: "number", failures: optional("number"), spark: numbers })) }),
  DistributionResult: object({ query, measure: "string", subject: optional("string"), unit: "string", count: "number", p50: optional("number"), p90: optional("number"),
    steps: array(object({ lower: "number", upper: optional("number"), count: "number" })) }),
  CalendarResult: object({ query, days: array(object({ date: "string", activeMs: "number", runs: "number", prompts: "number" })), maxActiveMs: "number" }),
  WeekHourResult: object({ query, weekdays: array("string"), activeMs: array(numbers), runs: array(numbers) }),
  SessionsResult: object({ query, sort: "string", rows: array(session), totalRows: "number", truncated: "boolean" }),
  ToolsResult: object({ query, buckets: array(bucket), rows: array(toolRow), kinds: array(keyCount), totalRows: "number", truncated: "boolean" }),
  ModelsResult: object({ query, buckets: array(bucket), rows: array(modelRow), totalRows: "number", truncated: "boolean",
    efforts: array(object({ provider: "string", model: "string", effort: "string", requests: "number", tokens: "number", reasoningShare: "number" })) }),
  ProjectsResult: object({ query, buckets: array(bucket), totalRows: "number", truncated: "boolean",
    rows: array(object({ project: "string", name: "string", sessions: "number", runs: "number", activeMs: "number", tokens: "number", toolCalls: "number", costs: array(cost), spark: numbers })) }),
  RecordsResult: object({ query, records: array(object({ measure: "string", subject: "string", value: "number", unit: "string", sessionId: optional("string"), runId: optional("string"), at: optional("string") })) }),
  HealthResult: object({ query, buckets: array(bucket), errors: numbers, interruptedRuns: numbers, runs: "number", errorRate: "number", failedTools: array(toolRow), compactionsByTrigger: array(keyCount), compactions: numbers,
    tokensBeforeCompaction: "number", tokensAfterCompaction: "number", contextByModel: array(object({ provider: "string", model: "string", average: optional("number"), highest: "number", samples: "number" })) }),
  SessionDetailResult: object({ query, session, children: array(session), totals: array(tile), models: array(modelRow), tools: array(toolRow),
    runs: array(object({ runId: "string", start: "string", durationMs: "number", outcome: "string", sender: "string", requests: "number", toolCalls: "number", inputTokens: "number", outputTokens: "number", model: "string" })) }),
  DetailsResult: object({ query, list: "string", rows: array(object({ name: "string", count: "number", share: "number" })), total: "number", totalRows: "number", truncated: "boolean" }),
  RunsResult: object({ query, sort: "string", totalRows: "number", truncated: "boolean",
    runs: array(object({ sessionId: "string", runId: "string", start: "string", durationMs: "number", outcome: "string", origin: "string", promptKind: "string", promptChars: "number", promptWords: "number", requests: "number", toolCalls: "number",
      toolFailures: "number", inputTokens: "number", outputTokens: "number", answerChars: "number", answerWords: "number", provider: "string", model: "string", effort: "string" })) }),
  StatisticsStatus: object({ state: "string", reason: optional("string"), choice: optional("string"), floorDay: optional("number"), sessionsTotal: "number", sessionsDone: "number", bytesTotal: "number", bytesDone: "number",
    oldestDateReached: optional("number"), completeFromDay: optional("number"), bytesPerSecond: optional("number"), etaSeconds: optional("number"), currentSessionId: optional("string"), skippedCount: "number",
    skipped: array(object({ sessionId: "string", reason: "string" })), pendingFlow: "number", revision: "number", error: optional("string"), isComplete: "boolean" }),
};

/** Returns what is wrong with `value` against `shape`, as paths; empty when it fits. */
function check(shape: Shape, value: unknown, path: string): string[] {
  if (shape === "any") return [];
  if (typeof shape === "string") return typeof value === shape ? [] : [`${path}: expected ${shape}, got ${JSON.stringify(value)}`];
  if ("optional" in shape) return value === undefined ? [] : check(shape.optional, value, path);
  if ("array" in shape) return Array.isArray(value) ? value.flatMap((item, index) => check(shape.array, item, `${path}[${index}]`)) : [`${path}: expected an array`];
  if (typeof value !== "object" || value === null || Array.isArray(value)) return [`${path}: expected an object`];
  const record = value as Record<string, unknown>, problems: string[] = [];
  for (const [name, field] of Object.entries(shape.object)) {
    if (!(name in record) && !(typeof field === "object" && "optional" in field)) problems.push(`${path}.${name}: missing`);
    else if (name in record) problems.push(...check(field, record[name], `${path}.${name}`));
  }
  for (const name of Object.keys(record)) if (!(name in shape.object)) problems.push(`${path}.${name}: not declared by the canvas`);
  return problems;
}

const golden = JSON.parse(readFileSync(fileURLToPath(new URL("../../../../CodeAlta.Plugin.Statistics.Tests/Golden/results.json", import.meta.url)), "utf8")) as Record<string, unknown>;

for (const [name, shape] of Object.entries(shapes)) {
  test(`${name}: the JSON of the plugin has the shape the canvas declares`, () => {
    assert.ok(name in golden, `the golden file has a sample of ${name}`);
    assert.deepEqual(check(shape, golden[name], name), []);
  });
}

test("a status before the choice has no date and no speed, and the request the canvas sends is read by the plugin", () => {
  const status = golden["StatisticsStatus.needsChoice"] as Record<string, unknown>;
  assert.equal(status.state, "needsChoice");
  assert.deepEqual(check(shapes.StatisticsStatus, status, "status"), []);
  assert.equal(status.oldestDateReached, undefined);
  // The request is written by the canvas in lower camel case (the plugin reads any case of an enum): its property names are the plugin's.
  const request = golden.StatisticsRequest as Record<string, unknown>;
  assert.deepEqual(Object.keys(request).sort(), ["comparison", "filter", "frequency", "limit", "period", "weekStart"]);
});

test("the golden file has a sample of every result the canvas types declare", () => {
  const declared = Object.keys(shapes);
  for (const name of declared) assert.ok(name in golden, name);
});
