import assert from "node:assert/strict";
import test from "node:test";
import type { SessionUsageObservation } from "#neoastra";
import { compactTokens, contextSegments, contextUsage, costText, groupedTokens, inputTokens, mergeUsageObservation, operationSegments, persistedContextUsage,
  persistedOperation, persistedUsageFields, rateWindowSummary, usageIntent, usageMarkdown, usageSegments } from "./contextUsage";

const record = "Idle\r\n\n**Context:** 124701 / 272000 tokens (45.8%)\r\n\n**Messages in context:** 145\r\n\n**Model:** gpt-test\r\n\n**Input tokens:** 88319\r\n\n**Output tokens:** 270\r\n\n**Cached input tokens:** 87936\r\n\n**Reasoning tokens:** 0\r\n\n**Duration:** 1500.5 ms\r\n\n**Usage scope/source:** CurrentWindow · ProviderUsage";
const observation = (patch: Partial<SessionUsageObservation> = {}): SessionUsageObservation => ({ sequence: "1", sourceUpdatedAt: null, eventTimestamp: null,
  scope: "CurrentWindow", source: "CodexTokenCountEvent", hadInvalidValues: false, hadOmittedData: false,
  window: { currentTokens: "100", tokenLimit: "1000", messageCount: 3, label: null, totalContextEnvelope: "1200", maxOutputTokens: null },
  lastOperation: { inputTokens: "10", outputTokens: "20", cacheReadTokens: null, cacheWriteTokens: null, cachedInputTokens: "5", reasoningTokens: null,
    cost: null, durationMs: null, model: "gpt-test", reasoningEffort: "high", initiator: null, label: null, costUnit: null },
  rateLimits: null, sessionTotal: null, ...patch });

test("token counts are shortened or grouped without losing Int64 precision", () => {
  assert.equal(compactTokens("0"), "0");
  assert.equal(compactTokens("999"), "999");
  assert.equal(compactTokens("1000"), "1k");
  assert.equal(compactTokens("1250"), "1.3k");
  assert.equal(compactTokens("124701"), "125k");
  assert.equal(compactTokens("272000"), "272k");
  assert.equal(compactTokens("1234567"), "1.2M");
  assert.equal(compactTokens("9223372036854775807"), "9223372037B");
  assert.equal(compactTokens("n/a"), "n/a");
  assert.equal(groupedTokens("9223372036854775807"), "9,223,372,036,854,775,807");
  assert.equal(groupedTokens("999"), "999");
  assert.equal(groupedTokens(null), "—");
});

test("occupancy needs a positive limit and is capped at 100%", () => {
  assert.deepEqual(contextUsage("124701", "272000"), { used: "124701", limit: "272000", percent: 45.8 });
  assert.deepEqual(contextUsage("300000", "272000"), { used: "300000", limit: "272000", percent: 100 });
  assert.deepEqual(contextUsage("500", null), { used: "500", limit: null, percent: null });
  assert.deepEqual(contextUsage("500", "0"), { used: "500", limit: null, percent: null });
  assert.equal(contextUsage(null, "272000"), null);
  assert.equal(contextUsage("abc", "272000"), null);
});

test("a persisted usage record yields the context line, its other fields and the last operation", () => {
  assert.deepEqual(persistedContextUsage(record), { used: "124701", limit: "272000", percent: 45.8 });
  assert.deepEqual(persistedContextUsage("**Context:** 1 000 / 2 000"), { used: "1000", limit: "2000", percent: 50 });
  assert.equal(persistedContextUsage("**Context:** unknown"), null);
  assert.equal(persistedContextUsage("no context here"), null);
  assert.equal(persistedContextUsage(null), null);
  assert.deepEqual(persistedUsageFields("**Context:** 1 / 2 tokens\r\n\n**Messages in context:** 145\r\n\n**Cost:** \r\n**Model:** gpt"),
    [{ label: "Messages in context", value: "145" }, { label: "Model", value: "gpt" }]);
  assert.deepEqual(persistedUsageFields(null), []);
  const operation = persistedOperation(record)!;
  assert.equal(operation.model, "gpt-test");
  assert.equal(operation.inputTokens, "88319");
  assert.equal(operation.cachedInputTokens, "87936");
  assert.equal(operation.reasoningTokens, "0");
  assert.equal(operation.durationMs, "1500.5");
  assert.equal(persistedOperation("**Context:** 1 / 2 tokens"), null);
});

test("a cost is shown with the unit the provider names, and as reported without one", () => {
  const operation = observation().lastOperation!;
  assert.equal(costText(operation), null);
  assert.equal(costText({ ...operation, cost: "0.01" }), "0.01");
  assert.equal(costText({ ...operation, cost: "0.0613625", costUnit: "AI credits" }), "0.0614 AI credits");
  assert.equal(costText({ ...operation, cost: "12", costUnit: "AI credits" }), "12 AI credits");
  assert.match(usageMarkdown({ provider: "copilot", model: "claude-test", usage: null, messages: null, window: null,
    operation: { ...operation, inputTokens: "400", cacheWriteTokens: "300", cost: "0.1728", costUnit: "AI credits" }, rateLimits: null, sessionTotal: null }),
    /input 400 \(cache 5 · cache write 300\) · output 20 · cost 0\.1728 AI credits/);
  // A saved record carries the unit in its cost line.
  const saved = persistedOperation("**Context:** 1 / 2 tokens\n\n**Cache write tokens:** 300\n\n**Cost:** 0.1728 AI credits")!;
  assert.equal(saved.cacheWriteTokens, "300");
  assert.equal(costText(saved), "0.1728 AI credits");
});

test("the meter turns warning at 75% and danger at 90%, as in the TUI", () => {
  assert.equal(usageIntent(null), "none");
  assert.equal(usageIntent(45.8), "success");
  assert.equal(usageIntent(74.9), "success");
  assert.equal(usageIntent(75), "warning");
  assert.equal(usageIntent(90), "danger");
});

test("breakdown slices keep only positive counts and sum to the whole", () => {
  assert.deepEqual(contextSegments(contextUsage("250", "1000")), [{ key: "active", tokens: "250", share: 25 }, { key: "headroom", tokens: "750", share: 75 }]);
  assert.deepEqual(contextSegments(contextUsage("2000", "1000")), [{ key: "active", tokens: "1000", share: 100 }]);
  assert.deepEqual(contextSegments(contextUsage("5", null)), []);
  assert.deepEqual(usageSegments([["a", "0"], ["b", null], ["c", "x"]]), []);
  assert.deepEqual(operationSegments(null), []);
});

test("the input of an operation holds what the cache read and wrote, and each token is in one slice", () => {
  const operation = observation().lastOperation!;
  const slices = (patch: Partial<typeof operation>) => operationSegments({ ...operation, ...patch }).map(segment => [segment.key, segment.tokens, segment.share]);
  // A request of 26,317 input tokens of which 26,003 came from the cache is cached at 99%, not at half.
  const cached = { inputTokens: "26317", cachedInputTokens: "26003", cacheWriteTokens: "312", outputTokens: "123" };
  assert.deepEqual(inputTokens({ ...operation, ...cached }), { total: "26317", uncached: "2", cacheRead: "26003", cacheWrite: "312" });
  assert.deepEqual(slices(cached), [["uncachedInput", "2", 0], ["output", "123", 0.5], ["cacheWrite", "312", 1.2], ["cachedInput", "26003", 98.3]]);
  // "Cache read" is another name of the cached input, never a count to add to it.
  assert.deepEqual(inputTokens({ ...operation, inputTokens: "2400", cachedInputTokens: "2000", cacheReadTokens: "2000", cacheWriteTokens: "300" }),
    { total: "2400", uncached: "100", cacheRead: "2000", cacheWrite: "300" });
  assert.deepEqual(inputTokens({ ...operation, inputTokens: "2400", cachedInputTokens: null, cacheReadTokens: "2000" })?.uncached, "400");
  // An older record counted the cache beside the input: its input is what was not cached.
  assert.deepEqual(inputTokens({ ...operation, inputTokens: "100", cachedInputTokens: "2000", cacheWriteTokens: "300" }),
    { total: "2400", uncached: "100", cacheRead: "2000", cacheWrite: "300" });
  // Without a cache the input is one slice under its plain name, and the reasoning is taken out of the output.
  assert.deepEqual(slices({ cachedInputTokens: null, reasoningTokens: "15" }), [["input", "10", 33.3], ["output", "5", 16.7], ["reasoning", "15", 50]]);
  assert.deepEqual(slices({}), [["uncachedInput", "5", 16.7], ["output", "20", 66.7], ["cachedInput", "5", 16.7]]);
  assert.equal(inputTokens({ ...operation, inputTokens: null, cachedInputTokens: null }), null);
  assert.equal(inputTokens(null), null);
});

test("fields accumulate across the events of one attachment; identity comes from the newest event", () => {
  const limits = { name: "Codex", planType: "pro", primary: { usedPercent: 40, resetsAt: null, windowDurationMinutes: "300" }, secondary: null };
  const rateOnly = observation({ sequence: "2", scope: "RateLimitOnly", window: null, lastOperation: null, rateLimits: limits });
  const merged = mergeUsageObservation(observation(), rateOnly);
  assert.equal(merged.sequence, "2");
  assert.equal(merged.scope, "RateLimitOnly");
  assert.equal(merged.window?.currentTokens, "100");
  assert.equal(merged.lastOperation?.model, "gpt-test");
  assert.equal(merged.rateLimits?.primary?.usedPercent, 40);
  const next = mergeUsageObservation(merged, observation({ sequence: "3", window: { currentTokens: "300", tokenLimit: null, messageCount: null, label: null, totalContextEnvelope: null, maxOutputTokens: "50" },
    rateLimits: { name: null, planType: null, primary: { usedPercent: 55, resetsAt: null, windowDurationMinutes: null }, secondary: null } }));
  assert.deepEqual(next.window, { currentTokens: "300", tokenLimit: "1000", messageCount: 3, label: null, totalContextEnvelope: "1200", maxOutputTokens: "50" });
  assert.deepEqual(next.rateLimits, { name: "Codex", planType: "pro", primary: { usedPercent: 55, resetsAt: null, windowDurationMinutes: "300" }, secondary: null });
  assert.equal(mergeUsageObservation(null, rateOnly), rateOnly);
});

test("nothing of a request is carried into the next one", () => {
  const summary = { ...observation().lastOperation!, model: "claude-test", inputTokens: "44000", outputTokens: "900", cacheWriteTokens: "40000", cachedInputTokens: "3000",
    cost: "0.31", costUnit: "AI credits", initiator: "compaction" };
  const next = { ...observation().lastOperation!, inputTokens: "30000", outputTokens: "120", cachedInputTokens: null, reasoningEffort: null };
  // The request after a summary request is not one of a compaction, and wrote nothing to a cache.
  const merged = mergeUsageObservation(observation({ lastOperation: summary }), observation({ sequence: "2", lastOperation: next }));
  assert.deepEqual(merged.lastOperation, next);
  assert.deepEqual(inputTokens(merged.lastOperation), { total: "30000", uncached: "30000", cacheRead: "0", cacheWrite: "0" });
  // What reports no tokens is not another request: it completes the last one.
  const completed = mergeUsageObservation(merged, observation({ sequence: "3", lastOperation: { ...next, inputTokens: null, outputTokens: null, model: null, durationMs: "1250" } }));
  assert.deepEqual(completed.lastOperation, { ...next, durationMs: "1250" });
});

test("rate windows and the Markdown copy carry only what was reported", () => {
  const english = { used: (percent: number) => `${percent}% used`, window: (minutes: string) => `${minutes}m window`, resets: (time: string) => `resets ${time}` };
  assert.equal(rateWindowSummary({ usedPercent: 40, resetsAt: null, windowDurationMinutes: "300" }, english), "40% used · 300m window");
  assert.equal(rateWindowSummary({ usedPercent: null, resetsAt: null, windowDurationMinutes: null }, english), "");
  const markdown = usageMarkdown({ provider: "codex", model: "gpt-test", usage: contextUsage("124701", "272000"), messages: 145,
    window: { totalContextEnvelope: "400000", maxOutputTokens: "128000" }, operation: observation().lastOperation,
    rateLimits: { name: "Codex", planType: null, primary: { usedPercent: 40, resetsAt: null, windowDurationMinutes: "300" }, secondary: null },
    sessionTotal: { totalTokens: "900", inputTokens: "500", outputTokens: "300", cachedInputTokens: "60", reasoningTokens: "40" } });
  assert.equal(markdown, ["# codex context usage", "", "- Model: gpt-test", "", "## Context usage: 145 messages", "",
    "- Compaction pressure: 124,701 / 272,000 input tokens (45.8%)", "- Indicative model limits: context window 400,000 tokens; max output 128,000 tokens",
    "- Last operation: gpt-test · effort high · input 10 (cache 5) · output 20", "", "## Limits", "", "- Limits: Codex · plan unknown",
    "- Primary: 40% used · 300m window", "", "## Provider-specific details", "",
    "- Session total: total 900 · input 500 · output 300 · cache 60 · reasoning 40"].join("\n"));
  assert.match(usageMarkdown({ provider: null, model: null, usage: null, messages: null, window: null, operation: null, rateLimits: null, sessionTotal: null }),
    /Waiting for usage data/);
});
