import type { FleetBudgetHoldView, FleetBudgetsOverview, FleetBudgetView, FleetNotificationView } from "#neoastra";
import type { MessageKey } from "../localization";

/** A scope a budget can limit, as the form offers it: its kind, its id, and how it is named. */
export type BudgetScopeOption = Readonly<{ value: string; kind: "person" | "machine" | "space" | "project"; id: string | null; label: MessageKey; name: string | null }>;

/** A text to show with the values it names. */
export type Phrase = Readonly<{ key: MessageKey; values: Readonly<Record<string, string>> }>;

/** The scopes of a server a budget can limit: all the work of the person, then each machine, space and project. */
export function budgetScopeOptions(overview: FleetBudgetsOverview): BudgetScopeOption[] {
  return [
    { value: "person", kind: "person", id: null, label: "All my work", name: null },
    ...overview.machines.map(machine => ({ value: `machine:${machine.id}`, kind: "machine" as const, id: machine.id, label: "Machine: {name}" as MessageKey, name: machine.name })),
    ...overview.spaces.map(space => ({ value: `space:${space.id}`, kind: "space" as const, id: space.id, label: "Space: {name}" as MessageKey, name: space.name })),
    ...overview.projects.map(project => ({ value: `project:${project.id}`, kind: "project" as const, id: project.id, label: "Project: {name}" as MessageKey, name: project.name })),
  ];
}

/** How the scope of a budget is named in its row. */
export function budgetScopeLabel(budget: FleetBudgetView): { key: MessageKey; name: string | null } {
  switch (budget.scopeKind) {
    case "person": return { key: "All my work", name: null };
    case "machine": return { key: "Machine: {name}", name: budget.scopeLabel };
    case "space": return { key: "Space: {name}", name: budget.scopeLabel };
    default: return { key: "Project: {name}", name: budget.scopeLabel };
  }
}

/** An amount of a budget in words: a number of tokens, short (1.2M), or dollars. */
export function formatAmount(measure: string, value: number, locale: string): string {
  if (measure === "cost") {
    const digits = value < 10 ? 2 : 0;
    return new Intl.NumberFormat(locale, { style: "currency", currency: "USD", minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value);
  }

  return new Intl.NumberFormat(locale, { notation: "compact", maximumFractionDigits: 1 }).format(value);
}

/** The share of its limit a budget used, from 0 to 1, for its bar. */
export function usedShare(budget: Pick<FleetBudgetView, "used" | "limit">): number {
  if (!(budget.limit > 0)) return 0;
  return Math.min(1, Math.max(0, budget.used / budget.limit));
}

/** The intent of the bar of a budget: calm, then warning from 80%, then danger once reached. */
export function budgetIntent(state: string): "primary" | "warning" | "danger" {
  return state === "reached" ? "danger" : state === "warning" ? "warning" : "primary";
}

/** The limit typed in the form: a positive number of tokens (whole) or dollars (cents); null when it is not one. */
export function parseLimit(text: string, measure: string): number | null {
  const normalized = text.trim().replace(/[\s_,]/g, "").replace(/^\$/, "");
  if (!/^\d+(\.\d+)?$/.test(normalized)) return null;
  const value = Number(normalized);
  if (!Number.isFinite(value) || value <= 0) return null;
  const rounded = measure === "cost" ? Math.round(value * 100) / 100 : Math.round(value);
  return rounded > 0 ? rounded : null;
}

/** The runs that wait for a budget now, the oldest first, so that the first run held is decided first. */
export function waitingHolds(overview: Pick<FleetBudgetsOverview, "holds">): FleetBudgetHoldView[] {
  return overview.holds.filter(hold => hold.status === "waiting").sort((left, right) => left.requestedAt.localeCompare(right.requestedAt) || left.id.localeCompare(right.id));
}

/** The notifications the person has not dismissed whose problem lasts, the oldest first. */
export function newNotifications(overview: Pick<FleetBudgetsOverview, "notifications">): FleetNotificationView[] {
  return overview.notifications.filter(notification => !notification.read && !notification.resolved)
    .sort((left, right) => left.createdAt.localeCompare(right.createdAt) || left.id.localeCompare(right.id));
}

/** What a notification says, in a sentence. */
export function notificationPhrase(notification: FleetNotificationView): Phrase {
  const budget = notification.budget ?? notification.subject ?? "";
  const machine = notification.machine ?? "";
  const subject = notification.subject ?? "";
  const detail = notification.detail ?? "";
  switch (notification.kind) {
    case "budget.warning": return { key: "{budget} used 80% of its limit.", values: { budget } };
    case "budget.reached": return { key: "{budget} reached its limit.", values: { budget } };
    case "approval": return { key: "A run on {machine} waits for your approval.", values: { machine } };
  }

  switch (notification.problem) {
    case "disk_low": return { key: "{machine} is low on disk space: {detail} MB left.", values: { machine, detail } };
    case "sign_in_failed": return { key: "The sign-in of {subject} failed on {machine}.", values: { machine, subject } };
    case "sign_in": return { key: "The sign-in of {subject} expires soon on {machine}.", values: { machine, subject } };
    case "offline": return { key: "{machine} has been offline for {detail} hours.", values: { machine, detail } };
    case "version_behind": return { key: "{machine} runs an older version: {detail}.", values: { machine, detail } };
    case "automation_failing": return { key: "An automation keeps failing on {machine}.", values: { machine } };
    case "policy_refusing": return { key: "The policy of {machine} refuses runs.", values: { machine } };
    case "access_request": return { key: "Someone of your organization asks to use {machine}: let them in or refuse on its page.", values: { machine } };
    default: return { key: "{machine} has a problem.", values: { machine } };
  }
}

/** The run a notification says waits for the person, which they take over from it; null for another notification. */
export function approvalRun(notification: FleetNotificationView): Readonly<{ automationId: string; machineId: string; runId: string }> | null {
  return notification.kind === "approval" && notification.subject && notification.machineId && notification.detail
    ? { automationId: notification.subject, machineId: notification.machineId, runId: notification.detail } : null;
}

/** The intent of a notification, from its severity. */
export function notificationIntent(severity: string): "primary" | "warning" | "danger" {
  return severity === "error" ? "danger" : severity === "warning" ? "warning" : "primary";
}
