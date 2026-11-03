import type { MachineMovePreview, MachinePairPreview, MachinePairResult, MachineView, WorkspaceProject, WorkspaceSession } from "#neoastra";
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

/** The state of the machine a row of the Explorer stands for; offline when the host does not say. */
export const machineRowState = (project: Pick<WorkspaceProject, "machineState">): MachineState => machineState({ state: project.machineState ?? "offline" });

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

/** A machine a session can move to: a paired machine, or this computer (`id` null). */
export interface MoveTarget { id: string | null; name: string; state: MachineState }

/**
 * Where a session can move: this computer for a session of a paired machine, and the paired machines it does not run
 * on, online ones first. A machine that is not online is listed, to say why it cannot be chosen.
 */
export function moveTargets(session: Pick<WorkspaceSession, "machineId">, machines: readonly MachineView[], thisComputer: string): MoveTarget[] {
  const targets: MoveTarget[] = session.machineId ? [{ id: null, name: thisComputer, state: "online" }] : [];
  for (const machine of orderedMachines(machines))
    if (machine.id !== session.machineId) targets.push({ id: machine.id, name: machine.name, state: machineState(machine) });
  return targets;
}

/** Why a session cannot move, or did not, in words: from the status or the code a preview or a move gives; null when nothing stops it. */
export function moveProblem(status: string | null | undefined, code?: string | null): MessageKey | null {
  const reason = status === "refused" || status === "aborted" ? code ?? status : status;
  switch (reason) {
    case undefined: case null: case "ok": case "accepted": case "finished": return null;
    case "offered": case "unreachable": case "machine_offline": return "The machine does not answer: the move goes on when it is back, and the session stays read-only meanwhile.";
    case "busy": case "working": case "queued": return "The session is at work: it moves once it is idle.";
    case "waiting": case "interaction_pending": return "The session waits for an answer: answer it before it moves.";
    case "background_running": return "Background jobs of the session run: they end before it moves.";
    case "project_missing": return "The machine has no project of this repository.";
    case "receive_disabled": return "This computer does not receive moved sessions.";
    case "forbidden": return "This computer may not move sessions to that machine.";
    case "no_remote": return "The repository has no remote: another machine cannot find it.";
    case "not_movable": case "not_repository": return "Only a session of a project in a git repository moves to another machine.";
    case "session_exists": return "The session is already on that machine.";
    case "moving": case "relinquished": return "The session is moving, or moved already.";
    case "clone_failed": return "The repository could not be cloned there.";
    case "payload_too_large": case "too_large": return "The work in progress is too large to move.";
    default: return "The session could not move.";
  }
}

/** Whether a preview lets the move start: an idle session, a target that takes it (or clones it when asked), this computer receiving moves. */
export function moveReady(preview: MachineMovePreview | null, targetIsHere: boolean, clone: boolean): boolean {
  if (!preview || preview.status !== "ok" || preview.busy) return false;
  if (targetIsHere && !preview.receiveMoves) return false;
  return preview.targetStatus === "accepted" || preview.targetCode === "project_missing" && preview.canClone && clone;
}
