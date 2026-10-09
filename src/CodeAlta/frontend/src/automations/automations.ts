// What the page knows of an automation without asking the host: how a trigger reads, what a form holds, the
// templates a new one can start from.
import type { AutomationInput, AutomationItem, AutomationRunItem, AutomationTriggerItem } from "#neoastra";
import type { IconName } from "../AppIcon";
import type { Locale, MessageKey } from "../localization";

export type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;
export type TriggerType = "hourly" | "daily" | "weekly" | "cron" | "issue" | "pull_request" | "jira" | "command";
export const triggerTypes: readonly TriggerType[] = ["hourly", "daily", "weekly", "cron", "issue", "pull_request", "jira", "command"];
/** The days as the host names them, Monday first as a week is shown. */
export const weekDays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;
export const maximumTriggers = 8;
export const maximumNameLength = 120;
export const maximumPromptLength = 32768;
export const maximumCommandLength = 2048;
export const maximumFolderLength = 1024;

const typeLabels: Readonly<Record<TriggerType, MessageKey>> = {
  hourly: "Hourly", daily: "Daily", weekly: "Weekly", cron: "Cron", issue: "Issue", pull_request: "Pull request", jira: "Jira issue", command: "Command",
};
const typeIcons: Readonly<Record<TriggerType, IconName>> = {
  hourly: "repeat", daily: "sun", weekly: "calendar", cron: "terminal", issue: "issueOpen", pull_request: "pullRequest", jira: "list", command: "tool",
};

export const triggerLabel = (type: string): MessageKey => typeLabels[type as TriggerType] ?? "Manual";
/** The icon of what starts an automation: of its first trigger, or the one of a run by hand. */
export const triggerIcon = (type: string | undefined): IconName => type && type in typeIcons ? typeIcons[type as TriggerType] : "hand";
/** The color of a kind of trigger, one of the tones of the application. */
export function triggerTone(type: string | undefined): string {
  switch (type) {
    case "hourly": return "azure";
    case "daily": return "gold";
    case "weekly": return "purple";
    case "cron": return "teal";
    case "issue": return "green";
    case "pull_request": return "orange";
    case "jira": return "blue";
    case "command": return "cyan";
    default: return "muted";
  }
}

/** Whether a trigger is an event of the repository of a project, or of its tracker: it needs a project. */
export const needsProject = (trigger: Readonly<{ type: string }>): boolean => trigger.type === "issue" || trigger.type === "pull_request" || trigger.type === "jira";
/** Whether a trigger is a time on the clock, as opposed to an event of the repository of a project or a command. */
export const isSchedule = (trigger: Readonly<{ type: string }>): boolean => !needsProject(trigger) && trigger.type !== "command";

/**
 * Whether an automation waits for the user before its triggers start it: it came with the configuration of its
 * project, which other people write, and was not allowed here as it is now.
 */
export const waitsToBeAllowed = (item: Readonly<{ allowed: boolean; triggers: readonly unknown[] }>): boolean => !item.allowed && item.triggers.length > 0;

/** A trigger with the values a new one of its kind starts with. */
export function newTrigger(type: TriggerType): AutomationTriggerItem {
  return { type, minute: 0, every: 1, at: type === "daily" || type === "weekly" ? ["09:00"] : [], days: type === "weekly" ? ["mon"] : [],
    expression: type === "cron" ? "0 9 * * 1-5" : null, event: type === "jira" ? "created" : "opened", authors: "trusted",
    command: type === "command" ? "" : null, folder: null };
}

/** The name of a day in the language of the page, short: "Mon". */
export function dayName(day: string, locale: Locale): string {
  const index = weekDays.indexOf(day as typeof weekDays[number]);
  if (index < 0) return day;
  // The 1st of January 2024 is a Monday.
  try { return new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" }).format(new Date(Date.UTC(2024, 0, 1 + index))); }
  catch { return day; }
}

/**
 * How one trigger reads: "Daily at 09:00". A command is named by its start, or read whole, with the folder it
 * names, where the user reads what an automation runs before allowing it.
 */
export function describeTrigger(trigger: AutomationTriggerItem, t: Translate, locale: Locale, whole = false): string {
  const times = trigger.at.join(", ");
  switch (trigger.type) {
    case "hourly": {
      const minute = `:${String(trigger.minute).padStart(2, "0")}`;
      return trigger.every > 1 ? t("Every {hours} hours at {minute}", { hours: trigger.every, minute }) : t("Every hour at {minute}", { minute });
    }
    case "daily": return t("Daily at {times}", { times });
    case "weekly": {
      const days = [...trigger.days].sort((a, b) => weekDays.indexOf(a as typeof weekDays[number]) - weekDays.indexOf(b as typeof weekDays[number]));
      return t("{days} at {times}", { days: days.map(day => dayName(day, locale)).join(", "), times });
    }
    case "cron": return t("Cron {expression}", { expression: trigger.expression ?? "" });
    case "issue": return t("When an issue is opened");
    case "pull_request": return t(trigger.event === "updated" ? "When a pull request is updated" : "When a pull request is opened");
    case "jira": return t(trigger.event === "updated" ? "When a Jira issue is updated" : "When a Jira issue is created");
    case "command": {
      const command = (trigger.command ?? "").trim();
      const folder = (trigger.folder ?? "").trim();
      if (whole) return t("When {command} succeeds", { command }) + (folder ? ` (${t("in")} ${folder})` : "");
      return t("When {command} succeeds", { command: command.length > 60 ? `${command.slice(0, 59)}…` : command });
    }
    default: return trigger.type;
  }
}

/** How the triggers of an automation read on one line; an automation without one is run by hand. */
export function describeTriggers(triggers: readonly AutomationTriggerItem[], t: Translate, locale: Locale): string {
  return triggers.length === 0 ? t("Manual") : triggers.map(trigger => describeTrigger(trigger, t, locale)).join(" · ");
}

export type RunTone = "running" | "ok" | "failed" | "muted";
/** How the status of a run is shown. */
export function runTone(status: string): RunTone {
  return status === "running" ? "running" : status === "completed" ? "ok" : status === "failed" ? "failed" : "muted";
}
const statusLabels: Readonly<Record<string, MessageKey>> = {
  running: "Running", completed: "Completed", failed: "Failed", cancelled: "Cancelled", interrupted: "Interrupted", skipped: "Skipped",
};
export const runStatusLabel = (status: string): MessageKey => statusLabels[status] ?? "Unknown";
/** What started a run, in words: a run by hand, or the kind of its trigger. */
export const runTriggerLabel = (trigger: string): MessageKey => trigger === "manual" ? "Manual" : triggerLabel(trigger);

/** An automation as its form holds it. */
export type AutomationForm = Readonly<{
  id: string | null; name: string; enabled: boolean; prompt: string;
  /** The project it runs in; null for a chat. */
  projectId: string | null;
  /** Whether it is written in the configuration of its project rather than of the user. */
  storeInProject: boolean;
  provider: string; model: string; effort: string; agent: string; catchUp: boolean;
  triggers: readonly AutomationTriggerItem[];
}>;

/** The form of a new automation, in a project or as a chat. */
export function emptyForm(projectId: string | null): AutomationForm {
  return { id: null, name: "", enabled: true, prompt: "", projectId, storeInProject: false, provider: "", model: "", effort: "", agent: "", catchUp: false, triggers: [] };
}

/** The form of an automation that exists. */
export function formOf(item: AutomationItem): AutomationForm {
  return { id: item.id, name: item.name, enabled: item.enabled, prompt: item.prompt, projectId: item.projectId, storeInProject: item.storeProjectId !== null,
    provider: item.provider ?? "", model: item.model ?? "", effort: item.effort ?? "", agent: item.agent ?? "", catchUp: item.catchUp, triggers: item.triggers };
}

/** What the form misses to be saved, as a message of the page; null when nothing. */
export function formProblem(form: AutomationForm): MessageKey | null {
  if (!form.name.trim()) return "Give the automation a name.";
  if (form.name.trim().length > maximumNameLength) return "The name is too long.";
  if (!form.prompt.trim()) return "Write the prompt the automation sends.";
  if (form.prompt.length > maximumPromptLength) return "The prompt is too long.";
  for (const trigger of form.triggers) {
    if (needsProject(trigger) && !form.projectId) return "A trigger on issues or pull requests needs a project.";
    if ((trigger.type === "daily" || trigger.type === "weekly") && trigger.at.length === 0) return "Add a time to the trigger.";
    if (trigger.type === "weekly" && trigger.days.length === 0) return "Choose a day for the weekly trigger.";
    if (trigger.type === "cron" && !(trigger.expression ?? "").trim()) return "Write the cron expression.";
    if (trigger.type === "command" && !(trigger.command ?? "").trim()) return "Write the command of the trigger.";
  }
  return null;
}

/** What is sent to the host for a form. */
export function inputOf(form: AutomationForm): AutomationInput {
  const provider = form.provider.trim();
  const model = provider ? form.model.trim() : "";
  return { id: form.id, name: form.name.trim(), enabled: form.enabled, prompt: form.prompt, projectId: form.projectId,
    provider: provider || null, model: model || null, effort: model && form.effort ? form.effort : null, agent: form.agent.trim() || null,
    // An automation that is run by hand misses nothing.
    catchUp: form.catchUp && form.triggers.length > 0,
    triggers: form.triggers.map(trigger => ({ ...trigger, at: [...trigger.at].sort(), expression: trigger.type === "cron" ? (trigger.expression ?? "").trim() : null,
      command: trigger.type === "command" ? (trigger.command ?? "").trim() : null, folder: trigger.type === "command" ? (trigger.folder ?? "").trim() || null : null })) };
}

export type AutomationScope = "all" | "chats" | "projects";
/** The automations a search and a scope keep, in the order of the host. */
export function filterAutomations(items: readonly AutomationItem[], query: string, scope: AutomationScope): readonly AutomationItem[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  return items.filter(item => (scope === "all" || (scope === "chats") === (item.projectId === null && !item.projectFolder))
    && words.every(word => `${item.name}\n${item.projectName ?? ""}\n${item.prompt}`.toLowerCase().includes(word)));
}

/** Where a time falls between two others, from 0 to 1. */
export function timelinePosition(at: string, from: number, to: number): number {
  const time = new Date(at).getTime();
  return !Number.isFinite(time) || to <= from ? 0 : Math.min(1, Math.max(0, (time - from) / (to - from)));
}

export type TimelineRow = Readonly<{ automationId: string; times: readonly string[]; dense: boolean }>;
/** From how many times in the day an automation is shown as running all along. */
export const denseTimeline = 24;
/** The times to come by automation, in the order each automation first comes. */
export function timelineRows(upcoming: readonly Readonly<{ automationId: string; at: string }>[]): readonly TimelineRow[] {
  const rows = new Map<string, string[]>();
  for (const time of upcoming) rows.set(time.automationId, [...rows.get(time.automationId) ?? [], time.at]);
  return [...rows].map(([automationId, times]) => ({ automationId, times, dense: times.length >= denseTimeline }));
}

/** The runs whose session the page can open: the session still exists. */
export function openableRun(run: AutomationRunItem, sessions: ReadonlySet<string>): boolean {
  return run.sessionId !== null && sessions.has(run.sessionId);
}

/**
 * What the line above a session started by an automation says: the automation, while it exists, and what started
 * this run, while the run is among the ones the page knows.
 */
export function sessionOrigin(sessionId: string, automationId: string, items: readonly AutomationItem[], runs: readonly AutomationRunItem[], t: Translate):
  Readonly<{ id: string | null; name: string | null; summary: string | null }> {
  const automation = items.find(item => item.id === automationId);
  if (!automation) return { id: null, name: null, summary: null };
  const run = runs.find(candidate => candidate.sessionId === sessionId);
  return { id: automation.id, name: automation.name, summary: run ? [t(runTriggerLabel(run.trigger)), run.detail].filter(Boolean).join(" · ") : null };
}

export type AutomationTemplate = Readonly<{
  key: string; icon: IconName; tone: string; name: MessageKey; description: MessageKey;
  /** Written in English, like the prompts of the application: the user edits it in the language of their choice. */
  prompt: string;
  triggers: readonly AutomationTriggerItem[];
  /** Whether it only makes sense in the repository of a project. */
  project: boolean;
}>;

const daily = (time: string): AutomationTriggerItem => ({ ...newTrigger("daily"), at: [time] });
const weekly = (day: string, time: string): AutomationTriggerItem => ({ ...newTrigger("weekly"), days: [day], at: [time] });

/** What a new automation can start from. The prompts are written for a session that starts with nothing else. */
export const automationTemplates: readonly AutomationTemplate[] = [
  { key: "issue-triage", icon: "issueOpen", tone: "green", name: "Issue triage", description: "Label, de-duplicate and prioritize the issues of the last day.", project: true,
    triggers: [daily("09:00")], prompt: "Review the issues opened or updated in this repository since yesterday. For each one, say what it is about, whether it duplicates another issue, how severe it is and which area of the code it concerns. Finish with a short prioritized list. Do not change any issue: report only." },
  { key: "first-response", icon: "ask", tone: "teal", name: "New issue first look", description: "Look into each new issue when it is opened.", project: true,
    triggers: [newTrigger("issue")], prompt: "A new issue was opened in this repository: it is named below. Read it, find the code it is about and say whether you can reproduce or confirm it. Report what you found and what a fix would involve. Do not push anything and do not comment on the issue." },
  { key: "pr-review", icon: "pullRequest", tone: "orange", name: "Pull request review", description: "Review each pull request when it is opened.", project: true,
    triggers: [newTrigger("pull_request")], prompt: "A pull request was opened in this repository: it is named below. Fetch it, read the change and review it: correctness first, then tests, then clarity. Report your findings ordered by importance, with the file and line of each one. Do not push anything and do not comment on the pull request." },
  { key: "changelog", icon: "notes", tone: "purple", name: "Changelog draft", description: "Draft the changelog of the week from the merged work.", project: true,
    triggers: [weekly("fri", "16:00")], prompt: "Draft the changelog of the last seven days of this repository from its commits and merged pull requests. Group the entries under Added, Changed and Fixed, one line each, written for a user of the project. Report the draft: do not edit any file." },
  { key: "test-health", icon: "checked", tone: "azure", name: "Test health", description: "Run the tests every morning and report what fails.", project: true,
    triggers: [daily("07:30")], prompt: "Build this repository and run its tests. Report the tests that fail with the reason of each failure and the change that most likely caused it. Do not fix anything: report only." },
  { key: "dependencies", icon: "plugin", tone: "gold", name: "Dependency review", description: "List the dependencies that have a newer version.", project: true,
    triggers: [weekly("mon", "09:00")], prompt: "List the dependencies of this repository that have a newer version, with their current and latest versions and what changed between them. Point out the updates that fix a security issue and the ones that break compatibility. Do not update anything: report only." },
  { key: "repo-audit", icon: "search", tone: "muted", name: "Repository audit", description: "Look for dead code, stale documentation and missing tests.", project: true,
    triggers: [], prompt: "Audit this repository: look for dead code, documentation that no longer matches the code, and code without tests. Report the ten findings that matter most, each with the file it is in and what to do about it. Do not change any file." },
  { key: "daily-brief", icon: "chat", tone: "teal", name: "Daily brief", description: "A chat that summarizes what your projects did yesterday.", project: false,
    triggers: [daily("08:30")], prompt: "List my CodeAlta projects and their sessions with the alta command, and summarize what happened in them since yesterday: what was finished, what is still running and what needs my attention. Keep it to a short list." },
];
