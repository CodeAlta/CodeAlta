import type { MachineNewSessionChoices, MachineNewSessionRequest, MachineNewSessionResult, MachineView } from "#neoastra";
import type { MessageKey } from "../localization";
import { defaultSessionSettings, startRequest, type SessionSettings, type ShownModel } from "../sessionSettings/sessionSettings";
import { machineState, orderedMachines } from "./machines";
import type { MachineLoad } from "./seats";

/** Where the Run on chip of a new session can start it: this computer (`id` null) or a paired machine, with its load when it says it. */
export type RunOnTarget = Readonly<{ id: string | null; name: string; load?: MachineLoad }>;

/** Whether this device may start sessions on a machine (the permission it was given when it paired). */
export const canStartOn = (machine: Pick<MachineView, "permissions">): boolean => machine.permissions.includes("start");

/**
 * The places the Run on chip offers: this computer first, then the paired machines that are online and let this device
 * start sessions, online ones in the order of the Machines page. Only this computer when no machine can take a session:
 * the chip is then not shown.
 */
export function runOnTargets(machines: readonly MachineView[], thisMachine: string): RunOnTarget[] {
  return [{ id: null, name: thisMachine },
    ...orderedMachines(machines).filter(machine => machineState(machine) === "online" && canStartOn(machine)).map(machine => ({ id: machine.id, name: machine.name,
      load: { working: machine.working, queued: machine.queued, sessionLimit: machine.sessionLimit } }))];
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
 * chosen), and what it runs with, as the Agent chip of the composer chooses it among what the machine offers (a part
 * left null is the default the chip shows).
 */
export type RemoteSelection = Readonly<{ projectId: string | null | undefined; settings: SessionSettings }>;

/** Nothing chosen yet: the choices of the machine fill it in. */
export const noRemoteSelection: RemoteSelection = Object.freeze({ projectId: undefined, settings: defaultSessionSettings });

/**
 * The selection shown for the choices of a machine: the project the person chose while the machine still has it, else
 * the project the machine has of the same repository. No project is chosen for the person: without a suggestion, they
 * choose one. A provider the machine no longer offers goes back to its default, with its model, effort and mode.
 */
export function remoteSelection(choices: Pick<MachineNewSessionChoices, "projects" | "suggestedProjectId" | "providers"> | null, chosen: RemoteSelection): RemoteSelection {
  if (!choices) return chosen;
  const projectId = chosen.projectId === null || chosen.projectId !== undefined && choices.projects.some(project => project.id === chosen.projectId)
    ? chosen.projectId
    : choices.suggestedProjectId && choices.projects.some(project => project.id === choices.suggestedProjectId) ? choices.suggestedProjectId : undefined;
  const offered = chosen.settings.providerId === null || choices.providers.some(provider => provider.key === chosen.settings.providerId);
  const settings = offered ? chosen.settings : { ...chosen.settings, providerId: null, modelId: null, reasoningEffort: null, permissionMode: null };
  return { projectId, settings };
}

/**
 * Whether the new session on a machine offers to set a provider up there: the machine answered, and has no provider ready (#414).
 * A machine that does not answer, or refuses this computer, says that instead.
 */
export function offersProviderSetup(choices: Pick<MachineNewSessionChoices, "status" | "providers"> | null): boolean {
  return choices?.status === "ok" && choices.providers.length === 0;
}

/** Why a session cannot be started on the machine with that selection now; null when it can. `shownProvider` is the provider the Agent chip shows. */
export function remoteStartBlocker(choices: Pick<MachineNewSessionChoices, "status" | "providers"> | null, selection: RemoteSelection, shownProvider: string | null): MessageKey | null {
  if (!choices) return "Reading the machine…";
  if (choices.status !== "ok") return newSessionProblem(choices.status);
  if (!shownProvider || !choices.providers.some(provider => provider.key === shownProvider)) return "The machine has no provider ready.";
  if (selection.projectId === undefined) return "Choose a project of the machine.";
  return null;
}

/**
 * What a start on a machine asks of it, besides its project and prompt: the provider, the model and the effort the
 * Agent chip shows, and each other part the person chose; a part left to the defaults is the machine's.
 */
export function remoteStartRequest(selection: RemoteSelection, shown: ShownModel)
  : Pick<MachineNewSessionRequest, "providerKey" | "modelId" | "reasoningEffort" | "agentPromptId" | "permissionMode" | "sandbox" | "subSessions"> {
  const start = startRequest(selection.settings, shown);
  return { providerKey: start.providerId, modelId: start.providerId ? start.modelId : null, reasoningEffort: start.modelId ? start.reasoningEffort : null,
    agentPromptId: start.agentPromptId, permissionMode: start.permissionMode, sandbox: start.sandbox,
    subSessions: start.options?.subSessions === false ? false : null };
}

/**
 * What became of the first prompt of a session started on a machine: sent; held by a reached budget of the machine, which
 * sends it once its owner continues it anyway; queued, the machine running as many sessions as its owner lets it, sent once
 * its turn comes; or not sent, the prompt keeping its text here.
 */
export function remoteStartPrompt(status: MachineNewSessionResult["status"] | null | undefined): "sent" | "held" | "queued" | "not_sent" {
  return status === "ok" ? "sent" : status === "held" ? "held" : status === "queued" ? "queued" : "not_sent";
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
