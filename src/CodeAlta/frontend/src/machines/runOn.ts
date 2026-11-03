import type { MachineNewSessionChoices, MachineNewSessionResult, MachineView } from "#neoastra";
import type { MessageKey } from "../localization";
import { machineState, orderedMachines } from "./machines";

/** Where the Run on chip of a new session can start it: this computer (`id` null) or a paired machine. */
export type RunOnTarget = Readonly<{ id: string | null; name: string }>;

/** Whether this device may start sessions on a machine (the permission it was given when it paired). */
export const canStartOn = (machine: Pick<MachineView, "permissions">): boolean => machine.permissions.includes("start");

/**
 * The places the Run on chip offers: this computer first, then the paired machines that are online and let this device
 * start sessions, online ones in the order of the Machines page. Only this computer when no machine can take a session:
 * the chip is then not shown.
 */
export function runOnTargets(machines: readonly MachineView[], thisMachine: string): RunOnTarget[] {
  return [{ id: null, name: thisMachine },
    ...orderedMachines(machines).filter(machine => machineState(machine) === "online" && canStartOn(machine)).map(machine => ({ id: machine.id, name: machine.name }))];
}

/**
 * The machine a new session starts on: the machine of the row of the Explorer the person is in (its sessions of no
 * project of this computer), else the one the chip was set to while it is still offered, else this computer (null).
 */
export function runOnMachine(chosen: string | null, machines: readonly MachineView[], scopeMachineId: string | null): string | null {
  if (scopeMachineId) return scopeMachineId;
  if (!chosen) return null;
  const machine = machines.find(value => value.id === chosen);
  return machine && machineState(machine) === "online" && canStartOn(machine) ? chosen : null;
}

/**
 * What a new session on a machine is given: a project of that machine (null for a chat; undefined while none is
 * chosen), a provider of that machine and one of its models.
 */
export type RemoteSelection = Readonly<{ projectId: string | null | undefined; providerKey: string | null; modelId: string | null }>;

/** Nothing chosen yet: the choices of the machine fill it in. */
export const noRemoteSelection: RemoteSelection = Object.freeze({ projectId: undefined, providerKey: null, modelId: null });

/**
 * The selection shown for the choices of a machine: what the person chose while the machine still offers it, else the
 * project the machine has of the same repository, its default provider (else its first one), and the default model of
 * that provider (else its first one). No project is chosen for the person: without a suggestion, they choose one.
 */
export function remoteSelection(choices: Pick<MachineNewSessionChoices, "projects" | "suggestedProjectId" | "providers"> | null, chosen: RemoteSelection): RemoteSelection {
  if (!choices) return chosen;
  const projectId = chosen.projectId === null || chosen.projectId !== undefined && choices.projects.some(project => project.id === chosen.projectId)
    ? chosen.projectId
    : choices.suggestedProjectId && choices.projects.some(project => project.id === choices.suggestedProjectId) ? choices.suggestedProjectId : undefined;
  const provider = choices.providers.find(value => value.key === chosen.providerKey) ?? choices.providers.find(value => value.isDefault) ?? choices.providers[0] ?? null;
  const models = provider?.models ?? [];
  const modelId = provider && provider.key === chosen.providerKey && models.some(model => model.id === chosen.modelId) ? chosen.modelId
    : models.find(model => model.id === provider?.defaultModelId)?.id ?? models[0]?.id ?? null;
  return { projectId, providerKey: provider?.key ?? null, modelId };
}

/** Why a session cannot be started on the machine with that selection now; null when it can. */
export function remoteStartBlocker(choices: Pick<MachineNewSessionChoices, "status" | "providers"> | null, selection: RemoteSelection): MessageKey | null {
  if (!choices) return "Reading the machine…";
  if (choices.status !== "ok") return newSessionProblem(choices.status);
  if (!selection.providerKey || !choices.providers.some(provider => provider.key === selection.providerKey)) return "The machine has no provider ready.";
  if (selection.projectId === undefined) return "Choose a project of the machine.";
  return null;
}

/** Why a new session on a machine was refused, or its choices could not be read, in words. */
export function newSessionProblem(status: MachineNewSessionChoices["status"] | MachineNewSessionResult["status"] | null | undefined): MessageKey {
  switch (status) {
    case "not_allowed": case "forbidden": return "This computer may not start sessions on that machine.";
    case "offline": case "connecting": case "machine_offline": return "The machine does not answer.";
    case "unauthorized": return "The machine refused this computer.";
    case "not_found": return "This machine is no longer paired.";
    case "unavailable": return "The machine no longer has that project or provider.";
    case "node.stopping": return "The machine is stopping.";
    default: return "The session could not be started on the machine.";
  }
}
