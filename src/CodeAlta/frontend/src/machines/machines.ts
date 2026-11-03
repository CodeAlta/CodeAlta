import type { MachinePairPreview, MachinePairResult, MachineView, WorkspaceProject, WorkspaceSession } from "#neoastra";
import type { MessageKey } from "../localization";

/** The connection state of a paired machine, as the host reports it. */
export type MachineState = "connecting" | "online" | "offline" | "unauthorized";

/** The state of a machine view; an unknown value is offline. */
export function machineState(machine: Pick<MachineView, "state">): MachineState {
  return machine.state === "online" || machine.state === "connecting" || machine.state === "unauthorized" ? machine.state : "offline";
}

/** The words of a state. */
export function machineStateLabel(state: MachineState): MessageKey {
  return state === "online" ? "Online" : state === "connecting" ? "Connecting…" : state === "unauthorized" ? "Not authorized" : "Offline";
}

/** The Blueprint intent of the dot of a state. */
export function machineIntent(state: MachineState): "success" | "warning" | "danger" | "none" {
  return state === "online" ? "success" : state === "connecting" ? "warning" : state === "unauthorized" ? "danger" : "none";
}

/** Whether a session runs on a paired machine, not on this computer. */
export const isRemoteSession = (session: Pick<WorkspaceSession, "machineId"> | null | undefined): boolean => !!session?.machineId;

/** Whether a row of the Explorer stands for a paired machine (its sessions of no project of this catalog). */
export const isMachineProject = (project: Pick<WorkspaceProject, "machineId"> | null | undefined): boolean => !!project?.machineId;

/** What a pasted text is as a pairing link: trimmed, and empty when it cannot be one. */
export function pairingLinkOf(text: string): string {
  const value = text.trim();
  return /^codealta-pair:\/\/\S+$/iu.test(value) && value.length <= 4096 ? value : "";
}

/** Why a preview or a pairing failed, in words; null when it did not. */
export function pairingProblem(status: MachinePairPreview["status"] | MachinePairResult["status"]): MessageKey | null {
  switch (status) {
    case "ok": return null;
    case "invalid_link": return "This is not a pairing link.";
    case "expired": return "This pairing link has expired.";
    case "link_refused": return "The machine refused this link: ask it for a new one.";
    case "refused": return "The machine refused this computer.";
    case "unreachable": return "The machine does not answer.";
    default: return "Machines unavailable";
  }
}

/**
 * The words of the key of a machine, cut in two lines of three, as `altanode pair` prints them on one: the person
 * compares them before pairing.
 */
export function pairingWords(words: string | null | undefined): string[] {
  return (words ?? "").split(/\s+/u).filter(Boolean);
}

/** The machines in the order of the page: online ones first, then by name. */
export function orderedMachines(machines: readonly MachineView[]): MachineView[] {
  const rank = (machine: MachineView) => ({ online: 0, connecting: 1, offline: 2, unauthorized: 3 })[machineState(machine)];
  return [...machines].sort((left, right) => rank(left) - rank(right) || left.name.localeCompare(right.name) || left.id.localeCompare(right.id));
}

/** The state of the machine of a session, from the machines the page knows; null for a session of this computer. */
export function sessionMachineState(session: Pick<WorkspaceSession, "machineId">, machines: readonly MachineView[]): MachineState | null {
  if (!session.machineId) return null;
  const machine = machines.find(value => value.id === session.machineId);
  return machine ? machineState(machine) : "offline";
}
