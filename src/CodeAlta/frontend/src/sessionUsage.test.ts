import assert from "node:assert/strict";
import test from "node:test";
import { createUsageReads, sessionTotalsAfterRead, usageRequest, validateUsage, usageMessage, type UsageTarget } from "./sessionUsage";
import type { SessionUsageRequest, SessionUsageResponse, SessionUsageSummary } from "#neoastra";

const target: UsageTarget = { epoch: "12345678-1234-1234-1234-123456789abc", sessionId: "one", scope: "project",
  projectId: "12345678-1234-1234-1234-123456789abd", expectedProjectPath: "/fixture/project" };
const base: SessionUsageResponse = { status: "ok", hostEpoch: target.epoch, sessionId: target.sessionId,
  runtimeInstanceId: "12345678-1234-1234-1234-123456789abe", attachmentGeneration: "9223372036854775807",
  omittedUsageEvents: "0", currentSession: null, observation: { sequence: "9223372036854775806", source: "CodexTokenCountEvent", scope: "CurrentWindow",
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

test("session totals validate independently of live observations and cannot pollute refusals", () => {
  const currentSession = { operation: { ...base.observation!.lastOperation!, inputTokens: "9223372036854775807" },
    costs: [{ cost: "0", costUnit: "AI credits" }, { cost: "1.25", costUnit: "USD" }] };
  const response = { ...base, status: "no_observation", observation: null, currentSession };
  assert.deepEqual(validateUsage(target, response)?.currentSession, currentSession);
  for (const invalid of [
    { ...currentSession, operation: { ...currentSession.operation, inputTokens: "9223372036854775808" } },
    { ...currentSession, costs: [{ cost: "NaN", costUnit: "USD" }] },
    { ...currentSession, costs: [{ cost: "1", costUnit: "line\nbreak" }] },
    { ...currentSession, costs: [{ cost: null, costUnit: null }] },
  ]) assert.equal(validateUsage(target, { ...response, currentSession: invalid }), null);
  assert.equal(validateUsage(target, { ...response, status: "scope_mismatch", runtimeInstanceId: null,
    attachmentGeneration: null, omittedUsageEvents: null }), null);
});

test("verified saved-session totals need runtime identity but never fabricate an attachment", () => {
  const saved = { ...base, status: "saved_session", observation: null, attachmentGeneration: null, omittedUsageEvents: null,
    currentSession: { operation: base.observation!.lastOperation!, costs: [{ cost: "1.25", costUnit: "usd" }] } };
  assert.equal(validateUsage(target, saved)?.currentSession?.costs[0].cost, "1.25");
  for (const invalid of [{ ...saved, runtimeInstanceId: null }, { ...saved, attachmentGeneration: "1" },
    { ...saved, observation: base.observation }, { ...saved, omittedUsageEvents: "0" },
    { ...saved, sessionId: "foreign" }, { ...saved, currentSession: { ...saved.currentSession, costs: [{ cost: "NaN", costUnit: "usd" }] } }])
    assert.equal(validateUsage(target, invalid), null);
  // A cost whose provider named no unit has none.
  assert.equal(validateUsage(target, { ...saved, currentSession: { ...saved.currentSession, costs: [{ cost: "4", costUnit: null }] } })?.currentSession?.costs[0].costUnit, null);
});

const totals: SessionUsageSummary = { operation: { ...base.observation!.lastOperation!, inputTokens: "1000" }, costs: [{ cost: "1.25", costUnit: null }] };

/** The reads of a session against a host that answers when the test says so. */
function usageReads() {
  const sent: { request: SessionUsageRequest; signal: AbortSignal; timeout: number; resolve(value: SessionUsageResponse): void; reject(error: Error): void }[] = [];
  const log: string[] = [];
  const reads = createUsageReads({ ...target, createdAt: "2026-09-25T10:00:00Z" }, (request, options) => new Promise((resolve, reject) =>
    sent.push({ request, signal: options.signal, timeout: options.timeoutMilliseconds, resolve, reject })), {
    started: () => log.push("started"), response: value => log.push(value.currentSession ? "totals" : "observation"),
    failed: () => log.push("failed"), ended: () => log.push("ended") });
  const settled = () => new Promise<void>(resolve => setImmediate(resolve));
  return { sent, log, reads, settled, asked: () => sent.map(read => read.request.includeCurrentSession) };
}

test("the totals of a session are asked for when the window opens and on a refresh, once each, never by a periodic read", async () => {
  assert.deepEqual(["periodic", "open", "refresh"].map(reason => usageRequest(target, reason as "periodic").includeCurrentSession), [false, true, true]);
  assert.deepEqual(usageRequest({ ...target, createdAt: "2026-09-25T10:00:00Z" }, "open"), { expectedHostEpoch: target.epoch, sessionId: "one", scope: "project",
    projectId: target.projectId, expectedProjectPath: "/fixture/project", includeCurrentSession: true, createdAt: "2026-09-25T10:00:00Z" });
  const { sent, log, reads, settled, asked } = usageReads();
  // The session is shown, then a run goes on: the reads the session makes by itself.
  reads.read("periodic");
  sent[0].resolve(base);
  await settled();
  reads.read("periodic");
  sent[1].resolve(base);
  await settled();
  assert.deepEqual(asked(), [false, false]);
  // The window opens: one read with the totals, however many periodic reads the run asks for meanwhile.
  reads.read("open");
  reads.read("periodic");
  reads.read("periodic");
  assert.deepEqual(asked(), [false, false, true]);
  assert.equal(sent[2].signal.aborted, false, "A periodic read does not cut the read of the journal short.");
  assert.ok(sent[2].timeout > sent[1].timeout);
  sent[2].resolve({ ...base, currentSession: totals });
  await settled();
  // The periodic reads that waited are one read, without the totals.
  assert.deepEqual(asked(), [false, false, true, false]);
  sent[3].resolve(base);
  await settled();
  reads.read("periodic");
  // Refresh usage: one more read with the totals, in the place of the periodic read in flight.
  reads.read("refresh");
  assert.equal(sent[4].signal.aborted, true);
  sent[4].resolve(base);
  sent[5].resolve({ ...base, currentSession: totals });
  await settled();
  assert.deepEqual(asked(), [false, false, true, false, false, true]);
  assert.deepEqual(log, ["started", "observation", "ended", "started", "observation", "ended", "started", "totals", "ended",
    "started", "observation", "ended", "started", "started", "totals", "ended"]);
});

test("a window that closes gives up the read of the totals, and a failed or aborted read says nothing late", async () => {
  const { sent, log, reads, settled, asked } = usageReads();
  reads.read("open");
  reads.read("periodic");
  reads.closeTotals();
  assert.equal(sent[0].signal.aborted, true);
  // What the run asked for meanwhile is read now, without the totals.
  assert.deepEqual(asked(), [true, false]);
  sent[0].resolve({ ...base, currentSession: totals });
  sent[1].reject(new Error("timeout"));
  await settled();
  assert.deepEqual(log, ["started", "ended", "started", "failed", "ended"]);
  // Nothing to give up when no totals are being read.
  reads.read("periodic");
  reads.closeTotals();
  assert.equal(sent[2].signal.aborted, false);
  reads.abort();
  reads.abort();
  assert.equal(sent[2].signal.aborted, true);
  sent[2].resolve(base);
  await settled();
  assert.deepEqual(log.slice(5), ["started", "ended"]);
  assert.equal(sent.length, 3);
});

test("the totals shown stay until a read brings others", () => {
  const later: SessionUsageSummary = { ...totals, costs: [{ cost: "2.5", costUnit: null }] };
  assert.equal(sessionTotalsAfterRead(null, base), null);
  assert.equal(sessionTotalsAfterRead(null, { ...base, currentSession: totals }), totals);
  // A periodic read, a read that failed on the host and a read without an answer bring none: the good ones stay.
  assert.equal(sessionTotalsAfterRead(totals, base), totals);
  assert.equal(sessionTotalsAfterRead(totals, { ...base, status: "no_observation", observation: null }), totals);
  assert.equal(sessionTotalsAfterRead(totals, null), totals);
  assert.equal(sessionTotalsAfterRead(totals, { ...base, currentSession: later }), later);
});
