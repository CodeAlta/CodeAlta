import type { MachineAgentsView, MachineDeviceView, MachineManagement, MachineUpdateView, MachineView } from "#neoastra";
import type { MessageKey } from "../localization";

/** A permission a device has on a machine, in the order the page shows them. */
export type DevicePermission = "view" | "drive" | "start" | "move" | "automate" | "manage";

/** The permissions, in their order. */
export const devicePermissions: readonly DevicePermission[] = ["view", "drive", "start", "move", "automate", "manage"];

/** The words of a permission: its short name, and what it lets a device do. */
export function permissionLabel(permission: DevicePermission): { name: MessageKey; description: MessageKey } {
  switch (permission) {
    case "view": return { name: "View", description: "See its sessions." };
    case "drive": return { name: "Drive", description: "Send prompts, answer questions and permission requests." };
    case "start": return { name: "Start", description: "Start sessions there." };
    case "move": return { name: "Move", description: "Move sessions there, and take them back." };
    case "automate": return { name: "Automate", description: "Run its automations there." };
    case "manage": return { name: "Manage", description: "Change what each device may do, its projects, its secrets and its updates." };
  }
}

/** Whether this computer manages a machine, as the machine said last. */
export const managesMachine = (machine: Pick<MachineView, "permissions">): boolean => machine.permissions.includes("manage");

/** Whether a device has a permission. */
export const hasPermission = (device: Pick<MachineDeviceView, "permissions">, permission: DevicePermission): boolean => device.permissions.includes(permission);

/** Whether the permissions of a device can be changed from the page: not once it is revoked (it pairs again first). */
export const permissionsEditable = (device: Pick<MachineDeviceView, "revokedAt">): boolean => !device.revokedAt;

/**
 * Whether taking a permission from a device needs the person to confirm it first: this computer giving up managing the
 * machine (it cannot take it back from here).
 */
export const confirmsLoss = (device: Pick<MachineDeviceView, "isThisComputer">, permission: DevicePermission): boolean =>
  device.isThisComputer && permission === "manage";

/** Why the page of a machine cannot be shown, in words; null when it can. */
export function managementProblem(status: MachineManagement["status"] | MachineUpdateView["status"] | string): MessageKey | null {
  switch (status) {
    case "ok": return null;
    case "not_manager": return "This computer does not manage this machine.";
    case "offline": case "machine_offline": case "connecting": return "The machine does not answer: it is offline.";
    case "unauthorized": return "The machine refused this computer.";
    case "not_found": return "This machine is no longer paired.";
    case "device.not_found": return "That device is no longer paired with the machine.";
    case "project.not_found": return "The machine no longer has that project.";
    case "project.has_sessions": return "The project still has sessions there: archive or delete them first.";
    case "project.clone_failed": return "The repository could not be cloned there.";
    case "secret.not_found": return "The machine no longer has that secret.";
    case "secret.unavailable": return "The machine cannot open its secrets.";
    case "invalid_params": case "invalid_request": return "The machine refused this value.";
    default: return "The machine could not do it.";
  }
}

/** Where an update of a machine stands, in words, with the version it names. */
export function updateSummary(result: Pick<MachineUpdateView, "status" | "target" | "running">): MessageKey {
  switch (result.status) {
    case "up_to_date": return "Up to date.";
    case "available": return "Version {version} is available.";
    case "staged": return "It switches to {version} once no session works.";
    case "unavailable": return "This machine does not update itself: update it with dotnet tool update.";
    default: return "The update did not go through.";
  }
}

/** The devices in the order of the page: this computer first, then the trusted ones by pairing, the revoked ones last. */
export function orderedDevices(devices: readonly MachineDeviceView[]): MachineDeviceView[] {
  const rank = (device: MachineDeviceView) => device.revokedAt ? 2 : device.isThisComputer ? 0 : 1;
  return [...devices].sort((left, right) => rank(left) - rank(right) || left.pairedAt.localeCompare(right.pairedAt) || left.id.localeCompare(right.id));
}

/** Whether agents of other machines may work on a machine: off until a space lets them in. */
export const agentsAllowed = (view: Pick<MachineAgentsView, "spaces">): boolean => view.spaces.some(space => space.allowed);

/**
 * The sandboxes the sessions agents of other machines start may be held to: their parent's (`""`) first, then the
 * profiles the machine offers, and the one its policy names when it is none of them.
 */
export function agentSandboxChoices(view: Pick<MachineAgentsView, "sandboxes" | "sandbox">): string[] {
  const choices = [""];
  for (const name of [...view.sandboxes, view.sandbox ?? ""]) {
    if (name && !choices.some(value => value.toLowerCase() === name.toLowerCase())) choices.push(name);
  }
  return choices;
}

/** Whether a change of the bypass of agents' sessions asks first: turning it on does, turning it off does not. */
export const confirmsBypass = (allowed: boolean): boolean => allowed;

/** What a text typed to add a project is: a repository to clone (a URL, or `host:owner/repo`), a folder of the machine, or nothing. */
export function projectSource(text: string): { kind: "clone" | "folder"; value: string } | null {
  const value = text.trim();
  if (!value) return null;
  if (/^(https?|ssh|git):\/\/\S+$/iu.test(value) || /^[\w.-]+@[\w.-]+:\S+$/u.test(value)) return { kind: "clone", value };
  if (/^(\/|[A-Za-z]:[\\/]|\\\\)/u.test(value)) return { kind: "folder", value };
  return null;
}
