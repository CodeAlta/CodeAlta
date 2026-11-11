import type { MachineAgentsView, MachineDeviceView, MachineManagement, MachineProviderStatusView, MachineUpdateView, MachineView } from "#neoastra";
import type { MessageKey } from "../localization";

/** A permission a device has on a machine, in the order the page shows them. */
export type DevicePermission = "view" | "drive" | "start" | "move" | "automate" | "models" | "manage";

/** The permissions, in their order. */
export const devicePermissions: readonly DevicePermission[] = ["view", "drive", "start", "move", "automate", "models", "manage"];

/** The words of a permission: its short name, and what it lets a device do. */
export function permissionLabel(permission: DevicePermission): { name: MessageKey; description: MessageKey } {
  switch (permission) {
    case "view": return { name: "View", description: "See its sessions." };
    case "drive": return { name: "Drive", description: "Send prompts, answer questions and permission requests." };
    case "start": return { name: "Start", description: "Start sessions there." };
    case "move": return { name: "Move", description: "Move sessions there, and take them back." };
    case "automate": return { name: "Automate", description: "Run its automations there." };
    case "models": return { name: "Models", description: "Use its model servers in sessions." };
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

/** How the connection of a machine to a server it joined stands, in words. */
export function serverLinkLabel(state: string): MessageKey {
  switch (state) {
    case "connected": return "Connected";
    case "waiting": return "Waits for its approval";
    case "revoked": return "Revoked";
    case "unknown": return "Not known to the server";
    default: return "Not connected";
  }
}

/** The color of the state of a server link: green connected, red revoked, orange otherwise. */
export function serverLinkIntent(state: string): "success" | "danger" | "warning" {
  return state === "connected" ? "success" : state === "revoked" ? "danger" : "warning";
}

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
    case "provider.not_found": return "The machine no longer has that provider.";
    case "provider.sign_in_unsupported": return "This provider does not sign in with a code: sign in on the machine itself.";
    case "provider.sign_in_failed": return "The sign-in could not start on the machine.";
    case "provider.unavailable": return "This machine cannot change its providers: update CodeAlta Node there.";
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

/** Whether a text can be a label of a machine: letters, digits, `-`, `_`, `.` and `:`, at most 64, as the machine keeps them. */
export const isMachineLabel = (label: string): boolean => /^[A-Za-z0-9_.:-]{1,64}$/.test(label);

/** Where a provider of a machine stands, as the machine says it; an unknown value is not ready. */
export type ProviderState = "ready" | "preparing" | "failed" | "not_ready" | "disabled";

/** The state of a provider of a machine. */
export function providerState(provider: Pick<MachineProviderStatusView, "state">): ProviderState {
  return provider.state === "ready" || provider.state === "preparing" || provider.state === "failed" || provider.state === "disabled" ? provider.state : "not_ready";
}

/** The words of the state of a provider. */
export function providerStateLabel(state: ProviderState): MessageKey {
  switch (state) {
    case "ready": return "Ready";
    case "preparing": return "Preparing…";
    case "failed": return "Failed";
    case "disabled": return "Off";
    default: return "Not ready";
  }
}

/** The Blueprint intent of the state of a provider. */
export function providerStateIntent(state: ProviderState): "success" | "warning" | "danger" | "none" {
  return state === "ready" ? "success" : state === "failed" ? "danger" : state === "not_ready" ? "warning" : "none";
}

/**
 * Why a provider of a machine is not ready, in words, with the secret or the variable it waits for: what the machine skipped
 * it for (a missing key, a sign-in), said here instead of only in its log. Null when it is ready, prepares, or is off.
 */
export function providerProblem(provider: Pick<MachineProviderStatusView, "state" | "reason" | "detail">): { key: MessageKey; values?: Record<string, string> } | null {
  const state = providerState(provider);
  if (state !== "not_ready" && state !== "failed") return null;
  switch (provider.reason) {
    case "secret_missing": return { key: "Its key is the secret {name} of the machine, which is not set.", values: { name: provider.detail ?? "?" } };
    case "key_missing": return provider.detail ? { key: "It has no key: the variable {name} is not set on the machine.", values: { name: provider.detail } } : { key: "It has no key." };
    case "sign_in_needed": return { key: "It is not signed in on the machine." };
    case "start_failed": return provider.detail ? { key: "It did not start: {detail}", values: { detail: provider.detail } } : { key: "It did not start." };
    default: return { key: "The machine left it out: its log says why." };
  }
}

/** What the row of a provider offers to make it ready: its device-code sign-in, its key, or nothing (its own login on the machine). */
export function providerFix(provider: Pick<MachineProviderStatusView, "credential" | "signIn">): "sign_in" | "key" | null {
  return provider.credential === "sign_in" ? provider.signIn ? "sign_in" : null : provider.credential === "key" ? "key" : null;
}

/** The key a new provider of a type takes on a machine: the type itself, or the type with the first free number. */
export function newProviderKey(type: string, existing: readonly string[]): string {
  const taken = new Set(existing.map(key => key.toLowerCase()));
  if (!taken.has(type.toLowerCase())) return type;
  for (let number = 2; ; number++) if (!taken.has(`${type}-${number}`.toLowerCase())) return `${type}-${number}`;
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
