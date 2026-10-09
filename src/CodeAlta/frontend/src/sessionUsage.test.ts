import assert from "node:assert/strict";
import test from "node:test";
import { validateUsage, usageMessage, type UsageTarget } from "./sessionUsage";
import type { SessionUsageResponse } from "#neoastra";

const target: UsageTarget = { epoch: "12345678-1234-1234-1234-123456789abc", sessionId: "one", scope: "project",
  projectId: "12345678-1234-1234-1234-123456789abd", expectedProjectPath: "/fixture/project" };
const base: SessionUsageResponse = { status: "ok", hostEpoch: target.epoch, sessionId: target.sessionId,
  runtimeInstanceId: "12345678-1234-1234-1234-123456789abe", attachmentGeneration: "9223372036854775807",
  omittedUsageEvents: "0", observation: { sequence: "9223372036854775806", source: "CodexTokenCountEvent", scope: "CurrentWindow",
    sourceUpdatedAt: null, eventTimestamp: "2026-09-25T10:00:00Z", hadInvalidValues: true, hadOmittedData: true,
    window: { currentTokens: "0", tokenLimit: "9223372036854775807", messageCount: null, label: null, totalContextEnvelope: null, maxOutputTokens: null },
    lastOperation: { inputTokens: null, outputTokens: "0", cacheReadTokens: null, cacheWriteTokens: null,
      cachedInputTokens: null, reasoningTokens: null, cost: "0.01", durationMs: null, model: null, reasoningEffort: null, initiator: null, label: null, costUnit: null },
    rateLimits: null, sessionTotal: null } };

test("usage validates exact bounded decimal strings, zero, unknown, flags and attachment replacement", () => {
  assert.equal(validateUsage(target, base)?.observation?.window?.currentTokens, "0");
  assert.equal(validateUsage(target, base)?.observation?.window?.tokenLimit, "9223372036854775807");
  assert.equal(validateUsage(target, base)?.observation?.window?.messageCount, null);
  assert.equal(validateUsage(target, { ...base, attachmentGeneration: "2", observation: { ...base.observation!, sequence: "1" } })?.attachmentGeneration, "2");
  assert.match(usageMessage("no_observation"), /No usage observation/);
  assert.match(usageMessage("metadata_incomplete"), /metadata/);
  assert.doesNotMatch(usageMessage("private/file/name"), /private/);
});

test("usage refuses foreign identity, malformed/overflow values, status pollution and missing evidence", () => {
  const rejects: unknown[] = [
    { ...base, sessionId: "two" }, { ...base, hostEpoch: "old" }, { ...base, runtimeInstanceId: "bad" },
    { ...base, attachmentGeneration: "9007199254740993.0" }, { ...base, attachmentGeneration: "9223372036854775808" },
    { ...base, omittedUsageEvents: -1 }, { ...base, observation: { ...base.observation, sequence: "0" } },
    { ...base, observation: { ...base.observation, source: "private" } },
    { ...base, observation: { ...base.observation, window: { currentTokens: 10, tokenLimit: "2", messageCount: 0 } } },
    { ...base, observation: { ...base.observation, lastOperation: { ...base.observation!.lastOperation, cost: "NaN" } } },
    { ...base, observation: { ...base.observation, hadOmittedData: "true" } },
    { ...base, status: "transition" },
    { ...base, status: "no_observation" },
    { ...base, observation: { ...base.observation, eventTimestamp: "raw diagnostic" } },
  ];
  for (const value of rejects) assert.equal(validateUsage(target, value), null);
  const empty = { ...base, status: "no_observation", observation: null };
  const rich = { ...base, observation: { ...base.observation!,
    window: { ...base.observation!.window!, label: "Active context window", totalContextEnvelope: "400000", maxOutputTokens: "128000" },
    lastOperation: { ...base.observation!.lastOperation!, model: "gpt-test", reasoningEffort: "high", initiator: "agent", label: "Last turn", costUnit: "AI credits" },
    rateLimits: { name: "Codex", planType: "pro", primary: { usedPercent: 40, resetsAt: "2026-09-25T12:00:00.0000000+00:00", windowDurationMinutes: "300" }, secondary: null },
    sessionTotal: { totalTokens: "900", inputTokens: "500", outputTokens: "300", cachedInputTokens: "60", reasoningTokens: "40" } } };
  assert.equal(validateUsage(target, rich)?.observation?.rateLimits?.primary?.usedPercent, 40);
  assert.equal(validateUsage(target, rich)?.observation?.sessionTotal?.totalTokens, "900");
  assert.equal(validateUsage(target, rich)?.observation?.lastOperation?.costUnit, "AI credits");
  for (const value of [
    { ...rich, observation: { ...rich.observation, lastOperation: { ...rich.observation.lastOperation, model: "m".repeat(129) } } },
    { ...rich, observation: { ...rich.observation, window: { ...rich.observation.window, label: "two\nlines" } } },
    { ...rich, observation: { ...rich.observation, lastOperation: { ...rich.observation.lastOperation, costUnit: "two\nlines" } } },
    { ...rich, observation: { ...rich.observation, window: { ...rich.observation.window, maxOutputTokens: "0" } } },
    { ...rich, observation: { ...rich.observation, rateLimits: { ...rich.observation.rateLimits, primary: { usedPercent: 101, resetsAt: null, windowDurationMinutes: null } } } },
    { ...rich, observation: { ...rich.observation, rateLimits: { ...rich.observation.rateLimits, planType: 5 } } },
    { ...rich, observation: { ...rich.observation, sessionTotal: { ...rich.observation.sessionTotal, totalTokens: "-1" } } },
  ]) assert.equal(validateUsage(target, value), null);
  assert.equal(validateUsage(target, empty)?.observation, null);
  assert.equal(validateUsage(target, { ...empty, attachmentGeneration: null }), null);
});
