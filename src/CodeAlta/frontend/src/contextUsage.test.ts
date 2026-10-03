import assert from "node:assert/strict";
import test from "node:test";
import type { SessionUsageObservation } from "#neoastra";
import { compactTokens, contextSegments, contextUsage, groupedTokens, mergeUsageObservation, operationSegments, persistedContextUsage,
  persistedOperation, persistedUsageFields, rateWindowSummary, usageIntent, usageMarkdown, usageSegments } from "./contextUsage";

const record = "Idle\r\n\n**Context:** 124701 / 272000 tokens (45.8%)\r\n\n**Messages in context:** 145\r\n\n**Model:** gpt-test\r\n\n**Input tokens:** 88319\r\n\n**Output tokens:** 270\r\n\n**Cached input tokens:** 87936\r\n\n**Reasoning tokens:** 0\r\n\n**Duration:** 1500.5 ms\r\n\n**Usage scope/source:** CurrentWindow · ProviderUsage";
const observation = (patch: Partial<SessionUsageObservation> = {}): SessionUsageObservation => ({ sequence: "1", sourceUpdatedAt: null, eventTimestamp: null,
  scope: "CurrentWindow", source: "CodexTokenCountEvent", hadInvalidValues: false, hadOmittedData: false,
  window: { currentTokens: "100", tokenLimit: "1000", messageCount: 3, label: null, totalContextEnvelope: "1200", maxOutputTokens: null },
  lastOperation: { inputTokens: "10", outputTokens: "20", cacheReadTokens: null, cacheWriteTokens: null, cachedInputTokens: "5", reasoningTokens: null,
    cost: null, durationMs: null, model: "gpt-test", reasoningEffort: "high", initiator: null, label: null },
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
  assert.deepEqual(operationSegments(observation().lastOperation).map(segment => [segment.key, segment.tokens]), [["input", "10"], ["output", "20"], ["cachedInput", "5"]]);
  assert.deepEqual(usageSegments([["a", "0"], ["b", null], ["c", "x"]]), []);
  assert.deepEqual(operationSegments(null), []);
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
    "- Last operation: gpt-test · effort high · input 10 · output 20 · cache 5", "", "## Limits", "", "- Limits: Codex · plan unknown",
    "- Primary: 40% used · 300m window", "", "## Provider-specific details", "",
    "- Session total: total 900 · input 500 · output 300 · cache 60 · reasoning 40"].join("\n"));
  assert.match(usageMarkdown({ provider: null, model: null, usage: null, messages: null, window: null, operation: null, rateLimits: null, sessionTotal: null }),
    /Waiting for usage data/);
});
