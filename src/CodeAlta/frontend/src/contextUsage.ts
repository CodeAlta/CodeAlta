import type { SessionUsageObservation, SessionUsageOperation, SessionUsageRateWindow } from "#neoastra";

/** Context-window occupancy for the compact composer meter. Token counts stay decimal strings (Int64-safe). */
export type ContextUsage = Readonly<{ used: string; limit: string | null; percent: number | null }>;

/** "1234567" -> "1.2M", "125000" -> "125k", "999" -> "999". Malformed input is returned unchanged. */
export function compactTokens(value: string): string {
  if (!/^\d+$/.test(value)) return value;
  const count = BigInt(value);
  const scaled = (unit: bigint, suffix: string) => {
    // Three significant digits at most: one rounded decimal below 100 units, a rounded whole number above.
    if (count >= unit * 100n) return `${(count + unit / 2n) / unit}${suffix}`;
    const tenths = (count * 10n + unit / 2n) / unit;
    return `${tenths / 10n}${tenths % 10n !== 0n ? `.${tenths % 10n}` : ""}${suffix}`;
  };
  return count >= 1_000_000_000n ? scaled(1_000_000_000n, "B") : count >= 1_000_000n ? scaled(1_000_000n, "M")
    : count >= 1_000n ? scaled(1_000n, "k") : value;
}

/** "1234567" -> "1,234,567" without going through Number. Unknown is a dash; malformed input is returned unchanged. */
export function groupedTokens(value: string | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return /^\d+$/.test(value) ? value.replace(/\B(?=(\d{3})+(?!\d))/g, ",") : value;
}

/** Occupancy from a reported window; a missing or zero limit leaves the percentage unknown. */
export function contextUsage(currentTokens: string | null | undefined, tokenLimit: string | null | undefined): ContextUsage | null {
  if (!currentTokens || !/^\d+$/.test(currentTokens)) return null;
  const limit = tokenLimit && /^\d+$/.test(tokenLimit) && BigInt(tokenLimit) > 0n ? tokenLimit : null;
  // Per-mille integer math keeps Int64 values exact; the meter never shows more than 100%.
  const percent = limit ? Math.min(100, Number(BigInt(currentTokens) * 1000n / BigInt(limit)) / 10) : null;
  return { used: currentTokens, limit, percent };
}

/** Reads "124,701 / 272,000" style text from a persisted usage record ("**Context:** ..."). */
export function persistedContextUsage(text: string | null | undefined): ContextUsage | null {
  const context = text?.match(/\*\*Context:\*\*\s*([^\n]+)/i)?.[1];
  const numbers = context?.match(/([\d][\d,._\s]*)\s*\/\s*([\d][\d,._\s]*)/);
  if (!numbers) return null;
  const digits = (value: string) => value.replace(/[^\d]/g, "");
  return contextUsage(digits(numbers[1]), digits(numbers[2]));
}

/** Meter color: calm until the window is mostly used (the TUI's thresholds). */
export function usageIntent(percent: number | null): "none" | "success" | "warning" | "danger" {
  return percent === null ? "none" : percent >= 90 ? "danger" : percent >= 75 ? "warning" : "success";
}

/** One "**Label:** value" line of a persisted usage record. */
export type PersistedUsageField = Readonly<{ label: string; value: string }>;

/** The labelled lines of a persisted usage record other than its context line, in recorded order. */
export function persistedUsageFields(text: string | null | undefined): PersistedUsageField[] {
  if (!text) return [];
  return [...text.matchAll(/\*\*([^*\r\n:]+):\*\*[ \t]*([^\r\n]*)/g)]
    .map(match => ({ label: match[1].trim(), value: match[2].trim() }))
    .filter(field => field.value !== "" && field.label.toLowerCase() !== "context");
}

/** The last operation of a persisted usage record, so saved sessions chart the same categories as live ones. */
export function persistedOperation(text: string | null | undefined): SessionUsageOperation | null {
  const fields = new Map(persistedUsageFields(text).map(field => [field.label.toLowerCase(), field.value]));
  const tokens = (label: string) => { const value = fields.get(label); return value && /^\d+$/.test(value) ? value : null; };
  const operation: SessionUsageOperation = { inputTokens: tokens("input tokens"), outputTokens: tokens("output tokens"), cacheReadTokens: null,
    cacheWriteTokens: tokens("cache write tokens"), cachedInputTokens: tokens("cached input tokens"), reasoningTokens: tokens("reasoning tokens"),
    cost: fields.get("cost") ?? null, durationMs: fields.get("duration")?.replace(/\s*ms$/i, "") ?? null,
    model: fields.get("model") ?? null, reasoningEffort: fields.get("reasoning effort") ?? null, initiator: null, label: null, costUnit: null };
  return Object.values(operation).some(value => value !== null) ? operation : null;
}

/**
 * The host reports only the last admitted usage event, and providers split usage across events (a
 * rate-limit update carries no window). Like the TUI, keep the newest value of every field seen on the
 * same attachment; identity, scope, source and flags always come from the newest event.
 */
export function mergeUsageObservation(current: SessionUsageObservation | null, incoming: SessionUsageObservation): SessionUsageObservation {
  if (!current) return incoming;
  const fields = <T extends object>(before: T | null | undefined, after: T | null | undefined): T | null => {
    if (!after) return before ?? null;
    if (!before) return after;
    const merged = { ...before } as Record<string, unknown>;
    for (const [key, value] of Object.entries(after)) if (value !== null && value !== undefined) merged[key] = value;
    return merged as T;
  };
  const limits = incoming.rateLimits && current.rateLimits ? {
    ...fields(current.rateLimits, incoming.rateLimits)!,
    primary: fields(current.rateLimits.primary, incoming.rateLimits.primary),
    secondary: fields(current.rateLimits.secondary, incoming.rateLimits.secondary),
  } : incoming.rateLimits ?? current.rateLimits ?? null;
  // An operation that reports tokens is another request: nothing of the request before it is carried over.
  const operation = incoming.lastOperation && (incoming.lastOperation.inputTokens !== null || incoming.lastOperation.outputTokens !== null)
    ? incoming.lastOperation : fields(current.lastOperation, incoming.lastOperation);
  return { ...incoming, window: fields(current.window, incoming.window), lastOperation: operation,
    rateLimits: limits, sessionTotal: incoming.sessionTotal ?? current.sessionTotal ?? null };
}

/** The cost of an operation with its unit when the provider names one ("0.0614 AI credits"), the reported number otherwise. */
export function costText(operation: Pick<SessionUsageOperation, "cost" | "costUnit"> | null | undefined): string | null {
  if (!operation?.cost) return null;
  if (!operation.costUnit) return operation.cost;
  const value = Number(operation.cost);
  return `${Number.isFinite(value) ? String(Number(value.toFixed(4))) : operation.cost} ${operation.costUnit}`;
}

/** A proportional slice of a breakdown bar. */
export type UsageSegment = Readonly<{ key: string; tokens: string; share: number }>;

/** Shares (0..100, one decimal) of the positive token counts, in the given order; empty when nothing is positive. */
export function usageSegments(parts: readonly (readonly [key: string, tokens: string | null | undefined])[]): UsageSegment[] {
  const positive = parts.filter((part): part is readonly [string, string] => !!part[1] && /^\d+$/.test(part[1]) && BigInt(part[1]) > 0n);
  const total = positive.reduce((sum, [, tokens]) => sum + BigInt(tokens), 0n);
  if (total === 0n) return [];
  return positive.map(([key, tokens]) => ({ key, tokens, share: Number((BigInt(tokens) * 1000n + total / 2n) / total) / 10 }));
}

/** Active context against the remaining input headroom, as the TUI's compaction-pressure chart shows it. */
export function contextSegments(usage: ContextUsage | null): UsageSegment[] {
  if (!usage?.limit) return [];
  const limit = BigInt(usage.limit);
  const used = BigInt(usage.used) > limit ? limit : BigInt(usage.used);
  return usageSegments([["active", used.toString()], ["headroom", (limit - used).toString()]]);
}

/** The input of one operation, split in the parts a provider bills apart: they add up to the total. */
export type InputTokens = Readonly<{ total: string; uncached: string; cacheRead: string; cacheWrite: string }>;

const count = (value: string | null | undefined) => value && /^\d+$/.test(value) ? BigInt(value) : null;

/**
 * Splits the input of an operation. The input holds what was read from and written to the prompt cache; a provider
 * names the read part "cached input" or "cache read", never both to add. A record with less input than cache is an
 * older one that counted the cache beside the input: there the input is what was not cached. Null when the operation
 * reports no input and nothing of the cache.
 */
export function inputTokens(operation: Pick<SessionUsageOperation, "inputTokens" | "cachedInputTokens" | "cacheReadTokens" | "cacheWriteTokens"> | null | undefined): InputTokens | null {
  if (!operation) return null;
  const read = count(operation.cachedInputTokens) ?? count(operation.cacheReadTokens) ?? 0n;
  const write = count(operation.cacheWriteTokens) ?? 0n;
  const reported = count(operation.inputTokens);
  if (reported === null && read + write === 0n) return null;
  const whole = reported !== null && reported >= read + write;
  const uncached = reported === null ? 0n : whole ? reported - read - write : reported;
  return { total: (uncached + read + write).toString(), uncached: uncached.toString(), cacheRead: read.toString(), cacheWrite: write.toString() };
}

/**
 * The token categories of one operation that the TUI charts, in its order. Each token is in one slice: the input is
 * shown without its cached parts, and the output without its reasoning.
 */
export function operationSegments(operation: SessionUsageOperation | null | undefined): UsageSegment[] {
  if (!operation) return [];
  const input = inputTokens(operation);
  const cached = !!input && (input.cacheRead !== "0" || input.cacheWrite !== "0");
  const reasoning = count(operation.reasoningTokens) ?? 0n;
  const output = count(operation.outputTokens);
  return usageSegments([[cached ? "uncachedInput" : "input", input?.uncached],
    ["output", output === null ? null : (output > reasoning ? output - reasoning : 0n).toString()],
    ["cacheWrite", input?.cacheWrite], ["cachedInput", input?.cacheRead], ["reasoning", operation.reasoningTokens]]);
}

/** "40% used · 300m window · resets 14:05:00", with only the parts that were reported. */
export function rateWindowSummary(window: SessionUsageRateWindow, text: { used: (percent: number) => string; window: (minutes: string) => string; resets: (time: string) => string }): string {
  const parts: string[] = [];
  if (window.usedPercent !== null && window.usedPercent !== undefined) parts.push(text.used(window.usedPercent));
  if (window.windowDurationMinutes) parts.push(text.window(window.windowDurationMinutes));
  if (window.resetsAt) {
    const date = new Date(window.resetsAt);
    if (!Number.isNaN(date.getTime())) parts.push(text.resets(date.toLocaleTimeString([], { hour12: false })));
  }
  return parts.join(" · ");
}

/** Canonical English Markdown of the usage window, with the sections and wording of the TUI copy. */
export function usageMarkdown(input: {
  provider: string | null; model: string | null; usage: ContextUsage | null; messages: number | null;
  window: { totalContextEnvelope: string | null; maxOutputTokens: string | null } | null;
  operation: SessionUsageOperation | null; rateLimits: SessionUsageObservation["rateLimits"]; sessionTotal: SessionUsageObservation["sessionTotal"];
}): string {
  const lines = [`# ${input.provider ?? "Session"} context usage`, "", `- Model: ${input.model ?? "(default model)"}`];
  const { usage, operation, window } = input;
  if (!usage && !operation) lines.push("- Status: Waiting for usage data from the active session.");
  else {
    lines.push("", input.messages !== null ? `## Context usage: ${input.messages} messages` : "## Context usage", "");
    if (usage) lines.push(`- Compaction pressure: ${usage.limit
      ? `${groupedTokens(usage.used)} / ${groupedTokens(usage.limit)} input tokens${usage.percent !== null ? ` (${usage.percent}%)` : ""}`
      : `${groupedTokens(usage.used)} tokens`}`);
    const envelope = [window?.totalContextEnvelope && `context window ${groupedTokens(window.totalContextEnvelope)} tokens`,
      window?.maxOutputTokens && `max output ${groupedTokens(window.maxOutputTokens)} tokens`].filter(Boolean);
    if (envelope.length) lines.push(`- Indicative model limits: ${envelope.join("; ")}`);
    if (operation) {
      // The input holds what the cache read and wrote: "input 26,317 (cache 26,003 · cache write 312)".
      const whole = inputTokens(operation);
      const cache = whole ? [whole.cacheRead !== "0" && `cache ${groupedTokens(whole.cacheRead)}`,
        whole.cacheWrite !== "0" && `cache write ${groupedTokens(whole.cacheWrite)}`].filter(Boolean) : [];
      const tokens = [operation.model, operation.reasoningEffort && `effort ${operation.reasoningEffort}`, operation.initiator && `initiator ${operation.initiator}`,
        whole && `input ${groupedTokens(whole.total)}${cache.length ? ` (${cache.join(" · ")})` : ""}`, operation.outputTokens && `output ${groupedTokens(operation.outputTokens)}`,
        operation.reasoningTokens && `reasoning ${groupedTokens(operation.reasoningTokens)}`,
        operation.durationMs && `duration ${operation.durationMs} ms`, operation.cost && `cost ${costText(operation)}`].filter(Boolean);
      if (tokens.length) lines.push(`- ${operation.label ?? "Last operation"}: ${tokens.join(" · ")}`);
    }
  }
  if (input.rateLimits) {
    const english = { used: (percent: number) => `${percent}% used`, window: (minutes: string) => `${minutes}m window`, resets: (time: string) => `resets ${time}` };
    lines.push("", "## Limits", "", `- Limits: ${input.rateLimits.name ?? "Rate limits"} · ${input.rateLimits.planType ?? "plan unknown"}`);
    if (input.rateLimits.primary) lines.push(`- Primary: ${rateWindowSummary(input.rateLimits.primary, english)}`);
    if (input.rateLimits.secondary) lines.push(`- Secondary: ${rateWindowSummary(input.rateLimits.secondary, english)}`);
  }
  if (input.sessionTotal) {
    const total = input.sessionTotal;
    lines.push("", "## Provider-specific details", "", `- Session total: total ${groupedTokens(total.totalTokens)} · input ${groupedTokens(total.inputTokens)} · output ${groupedTokens(total.outputTokens)} · cache ${groupedTokens(total.cachedInputTokens)} · reasoning ${groupedTokens(total.reasoningTokens)}`);
  }
  return lines.join("\n");
}
