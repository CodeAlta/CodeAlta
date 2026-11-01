import { translate } from "../localization";
import { choiceText, type SettingEntry } from "./settingsCatalog";
import type { SettingRead, SettingsHub, SettingWrite } from "./settingsHub";
import type { WindowSettingBinding } from "./windowSettings";

/** Reads and changes the value of a setting wherever it lives: the page for the settings it keeps, the host for the others. */
export type SettingValueSource = Readonly<{
  get(entry: SettingEntry, projectId?: string | null): Promise<SettingRead>;
  /** Changes a value; undefined gives the setting back its default. */
  set(entry: SettingEntry, value: unknown, projectId?: string | null): Promise<SettingWrite>;
}>;

/** Whether a change the host asks the page for has expired: `expires` is in milliseconds since 1970, as a text; none never expires. */
export function writeExpired(expires: string | null | undefined, now: number): boolean {
  if (!expires) return false;
  const deadline = Number(expires);
  return Number.isFinite(deadline) && now > deadline;
}

/** A value of a JSON text; null for none or for a text that is not JSON. */
export function parseSettingValue(text: string | null | undefined): unknown {
  if (text === null || text === undefined) return null;
  try { return JSON.parse(text) as unknown; } catch { return null; }
}

/**
 * The project a value of a setting is read for: the one of a plugin of a project, else the one named, else the selected
 * project for a setting a project can have; null for the user's.
 */
export function settingProject(entry: SettingEntry, projectId: string | null | undefined, selectedProjectId: string | null): string | null {
  if (entry.project) return entry.project;
  if (projectId !== undefined && projectId !== null) return projectId;
  return entry.scopes.includes("project") ? selectedProjectId : null;
}

/**
 * The values of the settings: those the page keeps are read and changed here, through their bindings, the others in the
 * host. A change of the host goes through the registry, which applies the rules of its page. A setting a project can
 * have is read and changed for the selected project, unless one is named.
 */
export function settingValueSource(hub: Pick<SettingsHub, "get" | "set">, window: ReadonlyMap<string, WindowSettingBinding>, selectedProjectId: string | null = null): SettingValueSource {
  return Object.freeze({
    async get(entry: SettingEntry, projectId?: string | null): Promise<SettingRead> {
      if (entry.owner === "window") {
        const binding = window.get(entry.id);
        return Object.freeze(binding
          ? { status: "ok", value: binding.value, source: JSON.stringify(binding.value) === JSON.stringify(entry.defaultValue) ? "default" : "window", message: null, choices: binding.choices ?? null }
          : { status: "unavailable", value: null, source: null, message: null, choices: null });
      }
      return hub.get(entry.id, settingProject(entry, projectId, selectedProjectId));
    },
    async set(entry: SettingEntry, value: unknown, projectId?: string | null): Promise<SettingWrite> {
      if (entry.owner === "window") {
        const binding = window.get(entry.id);
        if (!binding) return Object.freeze({ status: "unavailable", value: null, previous: null, message: null });
        const previous = binding.value;
        const error = binding.set(value === undefined ? entry.defaultValue : value);
        return Object.freeze(error ? { status: "invalid", value: null, previous, message: error } : { status: "ok", value: value === undefined ? entry.defaultValue : value, previous, message: null });
      }
      return hub.set(entry.id, value, settingProject(entry, projectId, selectedProjectId));
    },
  });
}

/** A value as the user reads it: the label of a choice, on or off, a number, the items of a list. */
export function settingValueText(locale: string, entry: SettingEntry, value: unknown): string {
  if (entry.kind === "boolean") return translate(locale, value === true ? "On" : "Off");
  if (entry.kind === "choice" && typeof value === "string") {
    const choice = entry.choices.find(candidate => candidate.value === value);
    return choice ? choiceText(locale, choice, entry.builtIn) : value;
  }
  if (Array.isArray(value)) return value.length ? value.map(String).join(", ") : translate(locale, "None");
  if (value === null || value === undefined || value === "") return translate(locale, "None");
  return typeof value === "object" ? JSON.stringify(value) : String(value);
}
