// Which microphone a dictation listens to. The choice is a preference of the window, kept by the page: the
// id of a device belongs to the profile of the WebView, so each instance of the application has its own.

/** An audio input as the browser lists it. */
export type InputDevice = Readonly<{ id: string; label: string }>;

/** The microphone the user chose, by the id and the label it had then; null is the one the system uses. */
export type MicrophoneChoice = InputDevice | null;

export type MicrophonePreference = Readonly<{
  device: MicrophoneChoice;
  /** The user has said which microphone to use: until then the first press of a microphone button asks. */
  confirmed: boolean;
}>;

export const microphoneStorageKey = "codealta.desktop.speech.microphone.v1";
export const defaultMicrophone: MicrophonePreference = Object.freeze({ device: null, confirmed: false });

// Chromium lists the device the system uses, and the one it uses for calls, as entries of their own that
// repeat a real device under these ids, with a prefix before its label.
const systemId = "default", callsId = "communications";
const pseudo = (id: string) => id === systemId || id === callsId;
const prefix = /^(Default|Communications) - /;
// The USB vendor and product of a device, which Chromium writes after its name: " (b58e:9e84)".
const hardware = / \([0-9a-f]{4}:[0-9a-f]{4}\)$/i;

const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit;

/** The preference as it was kept; anything that is not one reads as nothing chosen yet. */
export function readMicrophone(read: () => string | null): MicrophonePreference {
  try {
    const value: unknown = JSON.parse(read() ?? "null");
    if (!value || typeof value !== "object" || Array.isArray(value)) return defaultMicrophone;
    const { device, confirmed } = value as { device?: unknown; confirmed?: unknown };
    const chosen = device && typeof device === "object" && !Array.isArray(device) ? device as { id?: unknown; label?: unknown } : null;
    return { device: chosen && text(chosen.id, 512) && typeof chosen.label === "string" && chosen.label.length <= 512 && !pseudo(chosen.id) ? { id: chosen.id, label: chosen.label } : null,
      confirmed: confirmed === true };
  } catch { return defaultMicrophone; }
}

/** Keeps the preference; false when the page could not write it (it then holds until the window closes). */
export function saveMicrophone(write: (value: string) => void, preference: MicrophonePreference): boolean {
  try { write(JSON.stringify({ device: preference.device ? { id: preference.device.id, label: preference.device.label } : null, confirmed: preference.confirmed })); return true; }
  catch { return false; }
}

/** The name of a device as it is shown: without the prefix of a pseudo-entry and without its USB id. */
export function microphoneName(label: string): string {
  return label.replace(prefix, "").replace(hardware, "").trim();
}

/**
 * The name in a few words, for a tooltip: "Microphone (Yeti Stereo Microphone)" is "Yeti Stereo Microphone".
 * Only a single word before the brackets is taken as the kind of the device; "Microphone Array (Realtek)" stays whole.
 */
export function shortMicrophoneName(label: string): string {
  const name = microphoneName(label);
  const inner = /^\S+ \((.+)\)$/.exec(name);
  return inner ? inner[1].trim() : name;
}

/** One line of the list of microphones. */
export type MicrophoneEntry = Readonly<{ id: string; name: string;
  /** False for the chosen device when it is not there now: it stays in the list, marked. */
  connected: boolean }>;

export type MicrophoneList = Readonly<{
  /** The name of the device the system uses, when the browser tells it. */
  systemName: string | null;
  entries: readonly MicrophoneEntry[];
  /** The id of the entry that is chosen; empty for the microphone of the system. */
  chosen: string;
}>;

/**
 * The device to open for a choice: the one with its id, or, when the id is no longer listed (it can change
 * between two runs of the WebView), the one with its label. Null when the choice is the system's, or is not there.
 */
export function resolveMicrophone(devices: readonly InputDevice[], choice: MicrophoneChoice): InputDevice | null {
  if (!choice) return null;
  const real = devices.filter(device => device.id !== "" && !pseudo(device.id));
  return real.find(device => device.id === choice.id) ?? (choice.label ? real.find(device => device.label === choice.label) : undefined) ?? null;
}

/** What a list of microphones shows for the devices the browser lists and the choice of the user. */
export function listMicrophones(devices: readonly InputDevice[], choice: MicrophoneChoice): MicrophoneList {
  const system = devices.find(device => device.id === systemId && device.label !== "");
  const names = new Map<string, number>();
  const entries: MicrophoneEntry[] = devices.filter(device => device.id !== "" && !pseudo(device.id)).map((device, index) => {
    const name = microphoneName(device.label) || `#${index + 1}`;
    names.set(name, (names.get(name) ?? 0) + 1);
    return { id: device.id, name, connected: true };
  });
  // Two devices of the same model are told apart by their place.
  const seen = new Map<string, number>();
  const told = entries.map(entry => {
    if (names.get(entry.name) === 1) return entry;
    const place = (seen.get(entry.name) ?? 0) + 1;
    seen.set(entry.name, place);
    return { ...entry, name: `${entry.name} #${place}` };
  });
  const found = resolveMicrophone(devices, choice);
  if (choice && !found) told.push({ id: choice.id, name: microphoneName(choice.label) || choice.id, connected: false });
  return { systemName: system ? microphoneName(system.label) || null : null, entries: told, chosen: found?.id ?? choice?.id ?? "" };
}

/** The choice a list entry stands for: the device with its id as the browser lists it now, or the system's. */
export function microphoneChoice(devices: readonly InputDevice[], id: string, current: MicrophoneChoice): MicrophoneChoice {
  if (id === "") return null;
  const device = devices.find(candidate => candidate.id === id && !pseudo(candidate.id));
  // The entry of a device that is not connected is the choice that is already kept.
  return device ? { id: device.id, label: device.label } : current?.id === id ? current : null;
}

/**
 * Whether a capture that asked for one device and was refused should open the microphone of the system
 * instead: the device is not there. Any other refusal (no permission, no microphone at all, one that
 * another application holds) is the microphone that is not available.
 */
export function deviceMissing(errorName: string, exact: boolean): boolean {
  return exact && (errorName === "OverconstrainedError" || errorName === "NotFoundError");
}

/** How long a microphone whose level is shown may give nothing before a line says so; the line goes with the first sound. */
export const silenceDelay = 3_000;
/**
 * How long a dictation may hear nothing at all before a notice says so, once. Longer: a microphone with noise
 * removal gives exact silence until somebody speaks, and a user takes a moment to start.
 */
export const dictationSilenceDelay = 8_000;
/** The loudest sample (of 1) of a microphone that gives nothing: digital silence, which the noise of a quiet room is not. */
export const silencePeak = 1e-5;

/**
 * Whether a microphone gives no sound: the delay has passed, and no chunk of it had a sample above digital
 * silence. `peaks` are the loudest samples of the chunks heard so far; none at all is no sound either. Any
 * sound, however late, is a microphone that works.
 */
export function noSound(peaks: readonly number[], elapsed: number, delay = silenceDelay): boolean {
  return elapsed >= delay && peaks.every(peak => !(peak > silencePeak));
}
