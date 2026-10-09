import type { SessionUsageResponse } from "#neoastra";

export type UsageTarget = Readonly<{ epoch: string; sessionId: string; scope: "global" | "project";
  projectId: string | null; expectedProjectPath: string | null }>;

const max64 = 9223372036854775807n;
const scopes = new Set(["Unknown", "CurrentWindow", "LastOperation", "SessionTotal", "Compaction", "Truncation", "RateLimitOnly"]);
const sources = new Set(["Unknown", "CopilotSessionUsageInfo", "CopilotAssistantUsage", "CopilotAccountQuota",
  "CopilotCompactionComplete", "CopilotTruncation", "CodexSessionTokenUsageUpdated", "CodexTokenCountEvent",
  "CodexAccountRateLimitsUpdated", "RecoveredHistory", "LocalProviderUsage"]);
const failures = new Set(["invalid_request", "missing_session", "scope_mismatch", "transition", "stale_attachment",
  "metadata_missing", "metadata_incomplete", "metadata_invalid", "metadata_mismatch", "missing_project", "ambiguous_project",
  "incomplete_project", "invalid_project", "archived_project", "read_failed", "wire_limit", "closed", "stale_epoch"]);
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const decimal = (value: unknown, positive = false): value is string => typeof value === "string" &&
  /^(0|[1-9][0-9]{0,18})$/.test(value) && BigInt(value) <= max64 && (!positive || value !== "0");
const optionalDecimal = (value: unknown, positive = false) => value === null || decimal(value, positive);
const date = (value: unknown) => value === null || typeof value === "string" && value.length <= 48 &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value));
const finiteText = (value: unknown) => value === null || typeof value === "string" && value.length <= 32 &&
  /^(?:0|[1-9]\d*)(?:\.\d+)?(?:E[+-]?\d+)?$/.test(value) && Number.isFinite(Number(value));

// Provider text the host retains: bounded, single-line. Absent and null both mean "not reported".
const label = (value: unknown) => value == null || typeof value === "string" && value.length > 0 && value.length <= 128 && !/[\u0000-\u001f\u007f]/.test(value);
const rateWindow = (value: unknown) => value == null || object(value) &&
  (value.usedPercent == null || typeof value.usedPercent === "number" && Number.isInteger(value.usedPercent) && value.usedPercent >= 0 && value.usedPercent <= 100) &&
  date(value.resetsAt ?? null) && optionalDecimal(value.windowDurationMinutes ?? null);

// Never coerce an Int64 through Number. Failure replies have no observation or attachment identity.
export function validateUsage(target: UsageTarget, input: unknown): SessionUsageResponse | null {
  if (!object(input) || input.hostEpoch !== target.epoch || input.sessionId !== target.sessionId ||
    typeof input.status !== "string") return null;
  if (failures.has(input.status)) return input.runtimeInstanceId === null && input.attachmentGeneration === null &&
    input.omittedUsageEvents === null && input.observation === null ? input as unknown as SessionUsageResponse : null;
  if (input.status !== "ok" && input.status !== "no_observation" || !guid(input.runtimeInstanceId) ||
    !decimal(input.attachmentGeneration, true) || !decimal(input.omittedUsageEvents)) return null;
  if (input.status === "no_observation") return input.observation === null ? input as unknown as SessionUsageResponse : null;
  const usage = input.observation;
  if (!object(usage) || !decimal(usage.sequence, true) || !scopes.has(usage.scope as string) ||
    !sources.has(usage.source as string) || !date(usage.sourceUpdatedAt) || !date(usage.eventTimestamp) ||
    typeof usage.hadInvalidValues !== "boolean" || typeof usage.hadOmittedData !== "boolean") return null;
  if (usage.window !== null) {
    if (!object(usage.window) || !optionalDecimal(usage.window.currentTokens) || !optionalDecimal(usage.window.tokenLimit, true) ||
      !(usage.window.messageCount === null || typeof usage.window.messageCount === "number" &&
        Number.isInteger(usage.window.messageCount) && usage.window.messageCount >= 0 && usage.window.messageCount <= 2147483647) ||
      !label(usage.window.label) || !optionalDecimal(usage.window.totalContextEnvelope ?? null, true) ||
      !optionalDecimal(usage.window.maxOutputTokens ?? null, true)) return null;
  }
  if (usage.lastOperation !== null) {
    if (!object(usage.lastOperation) || !["inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens",
      "cachedInputTokens", "reasoningTokens"].every(key => optionalDecimal((usage.lastOperation as Record<string, unknown>)[key])) ||
      !finiteText(usage.lastOperation.cost) || !finiteText(usage.lastOperation.durationMs) ||
      !["model", "reasoningEffort", "initiator", "label", "costUnit"].every(key => label((usage.lastOperation as Record<string, unknown>)[key]))) return null;
  }
  if (usage.rateLimits != null) {
    if (!object(usage.rateLimits) || !label(usage.rateLimits.name) || !label(usage.rateLimits.planType) ||
      !rateWindow(usage.rateLimits.primary) || !rateWindow(usage.rateLimits.secondary)) return null;
  }
  if (usage.sessionTotal != null) {
    if (!object(usage.sessionTotal) || !["totalTokens", "inputTokens", "outputTokens", "cachedInputTokens", "reasoningTokens"]
      .every(key => decimal((usage.sessionTotal as Record<string, unknown>)[key]))) return null;
  }
  return input as unknown as SessionUsageResponse;
}

export function usageMessage(status: string): string {
  if (status === "missing_session") return "No existing actor for this session; no usage observation is available.";
  if (status === "no_observation") return "No usage observation has been admitted on this attachment.";
  if (status === "transition" || status === "stale_attachment") return "Attachment changed or is transitioning; no observation is available. Refresh explicitly.";
  if (status === "closed" || status === "stale_epoch") return "Host changed or closed. Reload before reading usage.";
  if (status === "scope_mismatch" || status === "invalid_request") return "Session scope could not be verified.";
  if (status.startsWith("metadata_")) return "Persisted session metadata is unavailable or inconsistent; usage withheld.";
  if (status.endsWith("_project")) return "Project ownership is unavailable or not verified; usage withheld.";
  return "Usage read unavailable; no observation was established. Refresh explicitly if needed.";
}
