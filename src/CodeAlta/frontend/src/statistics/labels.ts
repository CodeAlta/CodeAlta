import type { MessageKey } from "../localization";
import type { Comparison, Frequency, RequestFrequency } from "./types";
import type { FilterKey, PageId, PeriodPreset } from "./frame";

// The words of the frame and the pages: one function per kind of name, each a plain switch over literal keys, so that
// the translation table is checked at compile time and nothing here builds a key at run time.

/** A translation function. */
export type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;

/** The name of a page, as its tab reads. */
export function pageLabel(t: Translate, page: PageId): string {
  switch (page) {
    case "overview": return t("Overview");
    case "activity": return t("Activity");
    case "models": return t("Models");
    case "cost": return t("Cost");
    case "tools": return t("Tools");
    case "prompts": return t("Prompts");
    case "agents": return t("Agents");
    case "code": return t("Code");
    case "projects": return t("Projects");
    case "sessions": return t("Sessions");
    case "health": return t("Health");
  }
}

/** The name of a period the bar offers. */
export function periodLabel(t: Translate, preset: PeriodPreset): string {
  switch (preset) {
    case "today": return t("Today");
    case "7d": return t("Last 7 days");
    case "30d": return t("Last 30 days");
    case "90d": return t("Last 90 days");
    case "month": return t("This month");
    case "last-month": return t("Last month");
    case "year": return t("This year");
    case "all": return t("All time");
  }
}

/** The name of a frequency. */
export function frequencyLabel(t: Translate, frequency: RequestFrequency): string {
  switch (frequency) {
    case "auto": return t("Auto");
    case "hour": return t("Hour");
    case "day": return t("Day");
    case "week": return t("Week");
    case "month": return t("Month");
    case "year": return t("Year");
  }
}

/** The frequency as the adjective of a sentence about the buckets: "per day". */
export function perFrequency(t: Translate, frequency: Frequency): string {
  switch (frequency) {
    case "hour": return t("per hour");
    case "day": return t("per day");
    case "week": return t("per week");
    case "month": return t("per month");
    case "year": return t("per year");
  }
}

/** The name of a comparison. */
export function comparisonLabel(t: Translate, comparison: Comparison): string {
  switch (comparison) {
    case "none": return t("No comparison");
    case "previousPeriod": return t("Previous period");
    case "samePeriodLastYear": return t("Same period last year");
  }
}

/** What a comparison is called in the sentence "+12% compared with {period}". */
export function comparisonPhrase(t: Translate, comparison: Comparison): string {
  switch (comparison) {
    case "none": return t("the previous period");
    case "previousPeriod": return t("the previous period");
    case "samePeriodLastYear": return t("the same period last year");
  }
}

/** The name of a kind of filter. */
export function filterKindLabel(t: Translate, key: FilterKey): string {
  switch (key) {
    case "space": return t("Space");
    case "project": return t("Project");
    case "provider": return t("Provider");
    case "model": return t("Model");
    case "effort": return t("Reasoning effort");
    case "origin": return t("Started by");
    case "toolKind": return t("Kind of tool");
  }
}

/** The name of a value of a filter that has a fixed list. */
export function filterValueLabel(t: Translate, key: FilterKey, value: string): string {
  if (key === "origin") {
    switch (value) {
      case "you": return t("You");
      case "agent": return t("An agent");
      case "automation": return t("An automation");
      case "reminder": return t("A reminder");
    }
  }
  if (key === "toolKind") {
    switch (value) {
      case "files": return t("Files");
      case "search": return t("Search");
      case "shell": return t("Shell");
      case "web": return t("Web");
      case "alta": return t("alta commands");
      case "mcp": return t("MCP servers");
      case "skill": return t("Skills");
      case "other": return t("Other tools");
    }
  }
  return value;
}

/**
 * Names a provider from its key as the window names it (`claude-code` is "Claude Code"). A provider the window does not know, such as one
 * that was removed since its sessions ran, keeps its key.
 */
export function providerNamer(providers: readonly Readonly<{ key: string; name: string }>[] | undefined): (key: string) => string {
  const names = new Map((providers ?? []).filter(provider => provider.name.trim()).map(provider => [provider.key.toLowerCase(), provider.name.trim()]));
  return key => names.get(key.toLowerCase()) ?? key;
}

/** The name of an outcome of a run. */
export function outcomeLabel(t: Translate, outcome: string): string {
  switch (outcome) {
    case "completed": return t("Completed");
    case "failed": return t("Failed");
    case "interrupted": return t("Interrupted");
    case "running": return t("Running");
    default: return outcome;
  }
}

/** The name of who sent a prompt. */
export function senderLabel(t: Translate, sender: string): string {
  switch (sender) {
    case "you": return t("You");
    case "agent": return t("An agent");
    case "automation": return t("An automation");
    case "reminder": return t("A reminder");
    case "other": return t("Other");
    default: return sender;
  }
}

/** The name of a compaction trigger. */
export function triggerLabel(t: Translate, trigger: string): string {
  switch (trigger) {
    case "threshold": return t("Threshold");
    case "manual": return t("Manual");
    case "overflow": return t("Overflow");
    default: return trigger;
  }
}

/** The name of one of the records. */
export function recordLabel(t: Translate, measure: string): string {
  switch (measure) {
    case "longestRun": return t("Longest run");
    case "busiestDay": return t("Busiest day");
    case "mostToolCallsInRun": return t("Most tool calls in one run");
    case "longestStreak": return t("Longest streak");
    case "largestPrompt": return t("Largest prompt");
    case "longestTool": return t("Longest tool call");
    case "highestContextFill": return t("Fullest context");
    case "largestRequestInput": return t("Largest request");
    default: return measure;
  }
}

/** The name of a summary tile. */
export function tileLabel(t: Translate, id: string): string {
  switch (id) {
    case "sessions": return t("Sessions");
    case "runs": return t("Runs");
    case "active-time": return t("Active time");
    case "your-prompts": return t("Prompts you sent");
    case "tokens": return t("Tokens");
    case "requests": return t("Requests");
    case "tool-calls": return t("Tool calls");
    case "tool-failures": return t("Failed tool calls");
    case "lines-added": return t("Lines added");
    case "lines-removed": return t("Lines removed");
    case "files-changed": return t("Files changed");
    case "errors": return t("Errors");
    case "compactions": return t("Compactions");
    case "input-tokens": return t("Input tokens");
    case "output-tokens": return t("Output tokens");
    case "reasoning-tokens": return t("Reasoning tokens");
    default: return id;
  }
}

/** The name of a cost unit. */
export function unitLabel(t: Translate, unit: string): string {
  switch (unit) {
    case "usd": return t("US dollars");
    case "AI credits": return t("AI credits");
    default: return unit;
  }
}
