import type { Live, Machine, MachineCan, MachineState, Session } from "./contract";
import { ago } from "./sessionStore";

// The machines of the phone page, without the DOM: how a session is named by its machine and its id together (so that two
// machines never share one), the routes and addresses that follow from it, what each machine allows and offers, and how the
// machines are told to the person.

/** The id of the computer the page was served by. */
export const localMachineId = "local";

/** The choice of the machine list that shows the sessions of every machine. */
export const allMachines = "all";

/**
 * What a machine that tells nothing of its features is taken to offer. A remote machine offers no files of the phone, no files or
 * pictures of its disk, no review, no take-over and no choice of a worktree: they are not here, so that the phone leaves them out.
 */
export const remoteBaseline: readonly string[] = ["sessions.read", "provider", "archive", "jobs", "questions", "permissions"];

/** Whether a machine id is this computer's: none is, and so is `local`. */
export const isLocalMachine = (machineId: string | null | undefined): boolean => !machineId || machineId === localMachineId;

/**
 * The name of a session in the page: its id for a session of this computer (what the page always used, so that old links, drafts and
 * folds keep working), and `@<machine>/<id>` for one of another machine, the machine id encoded so that it holds no `/` and no `@`.
 * (An id of this computer never starts with `@`: they are GUIDs.)
 */
export function sessionKey(machineId: string | null | undefined, id: string): string {
  return isLocalMachine(machineId) ? id : `@${encodeURIComponent(machineId!)}/${id}`;
}

/** The machine and the id of a session name; a name without a machine is of this computer. */
export function splitKey(key: string): Readonly<{ machineId: string; id: string }> {
  const slash = key.indexOf("/");
  if (!key.startsWith("@") || slash <= 1) return { machineId: localMachineId, id: key };
  let machineId = key.slice(1, slash);
  try { machineId = decodeURIComponent(machineId); } catch { /* A machine id that was not encoded stays as it is. */ }
  return { machineId, id: key.slice(slash + 1) };
}

/** The machine of a session name. */
export const machineOf = (key: string | null | undefined): string => key ? splitKey(key).machineId : localMachineId;

/** The name of a session another session of the same machine links to or starts: an id that the machine of `from` gave. */
export const keyIn = (from: string | null | undefined, id: string): string => sessionKey(machineOf(from), id);

/** The address of the routes of a session: `/api/sessions/<id>`, or `/api/machines/<machine>/sessions/<id>` for another machine. */
export function sessionBase(key: string): string {
  const { machineId, id } = splitKey(key);
  return isLocalMachine(machineId)
    ? `/api/sessions/${encodeURIComponent(id)}`
    : `/api/machines/${encodeURIComponent(machineId)}/sessions/${encodeURIComponent(id)}`;
}

/** The route of the page that opens a session: `#/session/<id>` for this computer (the old links), `#/machine/<machine>/session/<id>` otherwise. */
export function sessionHash(key: string): string {
  const { machineId, id } = splitKey(key);
  return isLocalMachine(machineId) ? `#/session/${encodeURIComponent(id)}` : `#/machine/${encodeURIComponent(machineId)}/session/${encodeURIComponent(id)}`;
}

/** The name of the session a route opens, or null when the route opens none. */
export function parseSessionHash(hash: string): string | null {
  const decode = (text: string): string | null => { try { return decodeURIComponent(text); } catch { return null; } };
  const remote = /^#\/machine\/([^/]+)\/session\/(.+)$/u.exec(hash);
  if (remote) {
    const machineId = decode(remote[1]), id = decode(remote[2]);
    return machineId && id ? sessionKey(machineId, id) : null;
  }
  const local = /^#\/session\/(.+)$/u.exec(hash);
  return local ? decode(local[1]) : null;
}

/** The address of the live stream: the one stream of the page, which follows the display of a session of another machine when it is open. */
export function eventsUrl(openKey: string | null | undefined): string {
  if (!openKey) return "/api/events";
  const { machineId, id } = splitKey(openKey);
  if (isLocalMachine(machineId)) return "/api/events";
  return `/api/events?${new URLSearchParams({ machine: machineId, session: id })}`;
}

/**
 * The machine id as the page names it: `local` for this computer, whether the server says `local` or the id of this computer in the fleet
 * (`localDevice`, the `deviceId` of its entry of `GET /api/machines`, which a machine names a session's parent by).
 */
export const machineIdOf = (machineId: string | null | undefined, localDevice?: string | null): string | null | undefined =>
  localDevice && machineId === localDevice ? localMachineId : machineId;

/** A session name that names this computer by its id in the fleet (`@<device>/<id>`) becomes the plain name of its session. */
export function localKey(key: string, localDevice?: string | null): string {
  if (!localDevice) return key;
  const { machineId, id } = splitKey(key);
  return machineId === localDevice ? id : key;
}

/**
 * A session of another machine named by its machine and its id, and what it links to (its parent) on its own machine unless it says
 * otherwise. A session or a parent that is on this computer, whichever way the server names it, keeps its plain id: a sub-agent of
 * another machine then folds under its parent of this computer.
 */
export function qualifySession<T extends Session>(session: T, localDevice?: string | null): T {
  const machineId = machineIdOf(session.machineId, localDevice), parentMachineId = machineIdOf(session.parentMachineId, localDevice);
  const local = isLocalMachine(machineId), parentLocal = isLocalMachine(parentMachineId ?? machineId);
  if (local && parentLocal && machineId === session.machineId && parentMachineId === session.parentMachineId) return session;
  return {
    ...session,
    machineId, parentMachineId,
    id: sessionKey(machineId, session.id),
    parentId: session.parentId ? sessionKey(parentMachineId ?? machineId, session.parentId) : session.parentId,
  };
}

/** A live state of another machine's session, named as the session is. */
export function qualifyLive(live: Live, localDevice?: string | null): Live {
  const machineId = machineIdOf(live.machineId, localDevice);
  if (isLocalMachine(machineId)) return machineId === live.machineId ? live : { ...live, machineId };
  return { ...live, machineId, id: sessionKey(machineId, live.id) };
}

/** A page of a machine's sessions with its sessions (and the parents it adds) named by their machines. */
export function qualifyPage<T extends Readonly<{ sessions: readonly Session[]; parents?: readonly Session[] }>>(page: T, localDevice?: string | null): T {
  const qualify = (session: Session) => qualifySession(session, localDevice);
  return { ...page, sessions: (page.sessions ?? []).map(qualify), ...(page.parents ? { parents: page.parents.map(qualify) } : {}) };
}

/** A new random id of a command: sent again with the request when it is retried, so that the computer or the machine it goes to takes it once. */
export function newRequestId(source: Pick<Crypto, "randomUUID" | "getRandomValues"> | undefined = globalThis.crypto): string {
  if (typeof source?.randomUUID === "function") return source.randomUUID();
  const bytes = new Uint8Array(16);
  if (source?.getRandomValues) source.getRandomValues(bytes); else for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** The choice of the list when the person made none: every machine when there are several, else this computer. */
export const defaultSelection = (machines: readonly Machine[]): string => machines.length > 1 ? allMachines : localMachineId;

/** The choice kept on the phone, or the default one when the machine it names is gone or there is only this computer. */
export function validSelection(kept: string | null, machines: readonly Machine[]): string {
  if (machines.length <= 1) return localMachineId;
  if (kept === allMachines || (kept && machines.some(machine => machine.id === kept))) return kept!;
  return defaultSelection(machines);
}

/** How a machine is told in the list of machines: its state, and how it is reached or when it was last seen. */
export function machineStatus(machine: Pick<Machine, "state" | "path" | "lastSeen">, now: number): string {
  switch (machine.state) {
    case "online": return machine.path ? `Online · ${machine.path}` : "Online";
    case "connecting": return "Connecting…";
    case "offline": return machine.lastSeen ? `Offline · last seen ${ago(machine.lastSeen, now)}` : "Offline";
    case "unauthorized": return "Not allowed";
  }
}

/**
 * What a machine of the list cannot do for the phone, after its state: its sessions cannot be opened until CodeAlta is updated
 * there, or they are only read. Empty for a machine that is out of reach (its state says it) and for this computer.
 */
export function machineLimit(machine: Pick<Machine, "this" | "state" | "can" | "features">): string {
  if (machine.this || machine.state === "unauthorized") return "";
  if (!machine.features.includes("sessions.read")) return "Update CodeAlta on this machine";
  return machine.can.drive ? "" : "View only";
}

/** How many sessions of a machine wait for the person, in words; empty for none. */
export const waitingWords = (count: number): string => count <= 0 ? "" : count === 1 ? "1 waits for you" : `${count} wait for you`;

/** The facts of a session about its machine, as the list and the stream said them. */
export type MachineHints = Readonly<{ machine?: string | null; state?: MachineState | null; can?: Partial<MachineCan> | null; features?: readonly string[] | null }>;

/** What the phone may do with a session, from its machine: what is shown, what is only read and why, and what the machine is too old for. */
export type Ability = Readonly<{
  machineId: string;
  local: boolean;
  /** The name of the machine, null for this computer. */
  name: string | null;
  state: MachineState;
  canDrive: boolean;
  canStart: boolean;
  /** Whether the machine offers a feature; this computer offers them all. */
  offers: (feature: string) => boolean;
  /** Why the session can only be read (offline, not driven by the person...), or null when it can be driven. */
  readOnly: string | null;
  /** What to do when the machine runs a CodeAlta too old to read its sessions here, else null. */
  outdated: string | null;
}>;

const everything: Ability = { machineId: localMachineId, local: true, name: null, state: "online", canDrive: true, canStart: true, offers: () => true, readOnly: null, outdated: null };

/**
 * What the phone may do with the session `key`: on this computer everything, or what it says it offers once it lists machines
 * (an older CodeAlta lists none). On another machine, what its entry of
 * `GET /api/machines` says (else what the session said of it), and the features a remote machine offers when it says none.
 */
export function abilityOf(key: string | null | undefined, hints: MachineHints | null | undefined, machines: readonly Machine[], now: number): Ability {
  const machineId = machineOf(key);
  if (isLocalMachine(machineId)) {
    // This computer says what it offers once it lists machines: a node takes no review and gives no choice of a worktree.
    const self = machines.find(machine => machine.this || machine.id === localMachineId);
    if (!self?.features?.length) return everything;
    const drives = self.can?.drive !== false;
    return { ...everything, canDrive: drives, canStart: self.can?.start !== false, offers: feature => self.features.includes(feature),
      readOnly: drives ? null : "You can read this session, not drive it." };
  }
  const entry = machines.find(machine => machine.id === machineId);
  const name = entry?.name ?? hints?.machine ?? machineId;
  const state: MachineState = entry?.state ?? hints?.state ?? "online";
  const canDrive = entry?.can.drive ?? hints?.can?.drive ?? true, canStart = entry?.can.start ?? hints?.can?.start ?? true;
  const features = entry?.features ?? hints?.features ?? remoteBaseline;
  const offers = (feature: string) => features.includes(feature);
  const outdated = offers("sessions.read") ? null : `${name} runs a CodeAlta too old to open its sessions here. Update CodeAlta on ${name}.`;
  const seen = entry?.lastSeen ? ` (last seen ${ago(entry.lastSeen, now)})` : "";
  const readOnly = state === "offline" ? `${name} is offline${seen}: you can read this session, not drive it.`
    : state === "connecting" ? `Connecting to ${name}: you can read this session, not drive it yet.`
    : state === "unauthorized" ? `${name} does not let this computer in: you can read this session, not drive it.`
    : !canDrive ? `You can read this session on ${name}, not drive it.`
    : null;
  return { machineId, local: false, name, state, canDrive: canDrive && state === "online", canStart: canStart && state === "online", offers, readOnly, outdated };
}
