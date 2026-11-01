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

/** A value of a JSON text; null for none or for a text that is not JSON. */
export function parseSettingValue(text: string | null | undefined): unknown {
  if (text === null || text === undefined) return null;
  try { return JSON.parse(text) as unknown; } catch { return null; }
}

/**
 * The values of the settings: those the page keeps are read and changed here, through their bindings, the others in the
 * host. A change of the host goes through the registry, which applies the rules of its page.
 */
export function settingValueSource(hub: Pick<SettingsHub, "get" | "set">, window: ReadonlyMap<string, WindowSettingBinding>): SettingValueSource {
  return Object.freeze({
    async get(entry: SettingEntry, projectId: string | null = null): Promise<SettingRead> {
      if (entry.owner === "window") {
        const binding = window.get(entry.id);
        return Object.freeze(binding
          ? { status: "ok", value: binding.value, source: JSON.stringify(binding.value) === JSON.stringify(entry.defaultValue) ? "default" : "window", message: null, choices: binding.choices ?? null }
          : { status: "unavailable", value: null, source: null, message: null, choices: null });
      }
      return hub.get(entry.id, projectId);
    },
    async set(entry: SettingEntry, value: unknown, projectId: string | null = null): Promise<SettingWrite> {
      if (entry.owner === "window") {
        const binding = window.get(entry.id);
        if (!binding) return Object.freeze({ status: "unavailable", value: null, previous: null, message: null });
        const previous = binding.value;
        const error = binding.set(value === undefined ? entry.defaultValue : value);
        return Object.freeze(error ? { status: "invalid", value: null, previous, message: error } : { status: "ok", value: value === undefined ? entry.defaultValue : value, previous, message: null });
      }
      return hub.set(entry.id, value, projectId);
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
