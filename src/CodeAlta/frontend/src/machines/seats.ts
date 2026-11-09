import type { MessageKey } from "../localization";

/** The highest limit of sessions at work at once a machine takes (`FleetMachineLimits.MaxSessions`). */
export const maxSessionLimit = 256;

/** Reads the limit a person typed: a number from 1 to {@link maxSessionLimit}, null for none (an empty field), or `invalid`. */
export function parseSessionLimit(text: string): number | null | "invalid" {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  if (!/^\d+$/u.test(trimmed)) return "invalid";
  const value = Number(trimmed);
  return value >= 1 && value <= maxSessionLimit ? value : "invalid";
}

/** The load of a machine. */
export type MachineLoad = { readonly working: number | null; readonly queued: number | null; readonly sessionLimit: number | null };

/** The load of a machine in words, such as `3 of 3 at work, 2 queued`; null when the machine says nothing of it (an older one). */
export function loadParts(load: MachineLoad): readonly { key: MessageKey; vars: Record<string, string | number> }[] | null {
  if (load.working == null && load.queued == null && load.sessionLimit == null) return null;
  const working = load.working ?? 0;
  const parts: { key: MessageKey; vars: Record<string, string | number> }[] = [load.sessionLimit != null
    ? { key: "{working} of {limit} at work", vars: { working, limit: load.sessionLimit } }
    : { key: "{working} at work", vars: { working } }];
  if ((load.queued ?? 0) > 0) parts.push({ key: "{count} queued", vars: { count: load.queued ?? 0 } });
  return parts;
}

/** Whether a machine has a seat free now: no limit, or fewer seats taken than its limit and nothing that waits. */
export function hasFreeSeat(load: MachineLoad): boolean {
  if (load.sessionLimit == null) return true;
  return (load.working ?? 0) < load.sessionLimit && (load.queued ?? 0) === 0;
}

/** What a session whose run waits for a seat says: its machine and how many wait before it. */
export function queuedLabel(machine: string, position: number): { key: MessageKey; vars: Record<string, string | number> } {
  return position <= 1
    ? { key: "Queued on {machine} (next)", vars: { machine } }
    : { key: "Queued on {machine} ({count} before it)", vars: { machine, count: position - 1 } };
}
