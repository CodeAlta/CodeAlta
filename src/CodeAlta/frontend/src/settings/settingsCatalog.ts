import type { SettingsDescriptor } from "#neoastra";
import { messages, translate, type MessageKey } from "../localization";
import catalog from "./builtInSettings.json";

/** The kinds of a setting, as `builtInSettings.json` and the host name them. */
export type SettingKind = "boolean" | "choice" | "integer" | "number" | "text" | "path" | "list" | "complex" | "collection";

/** One choice of a setting: `label` is a text of the window (translated), `text` one shown as it is (the name of a language). */
export type SettingChoice = Readonly<{ value: string; label: string | null; literal: boolean }>;

/**
 * A setting as the window knows it: a built-in one of the catalog, with its availability from the host, or one a
 * plugin declares, whose texts are the plugin's own.
 */
export type SettingEntry = Readonly<{
  id: string; label: string; help: string | null; page: string; section: string | null; kind: SettingKind;
  choices: readonly SettingChoice[]; minimum: number | null; maximum: number | null; defaultValue: unknown;
  /** `host`, `window` (this page keeps it) or `plugin`. */
  owner: string;
  /** `readWrite` or `read`: what agents may do with it. */
  agents: string;
  /** False when it is changed on its page alone, which asks the user something first. */
  writable: boolean;
  managedBy: string | null; applies: string; scopes: readonly string[]; keywords: readonly string[];
  plugin: string | null; pluginName: string | null; available: boolean;
  /** Built in: its texts are keys of the dictionaries of the window. */
  builtIn: boolean;
}>;

type RawChoice = { value: string; label?: string; text?: string };
type RawSetting = { id: string; page: string; section?: string; label: string; help?: string; keywords?: string[]; kind: string; choices?: RawChoice[];
  minimum?: number; maximum?: number; default?: unknown; owner?: string; agents?: string; writable?: boolean; managedBy?: string; applies?: string; scopes?: string[] };

const choiceOf = (choice: RawChoice): SettingChoice => Object.freeze({ value: choice.value, label: choice.label ?? choice.text ?? null, literal: choice.label === undefined });

/** The built-in settings, in the order of the pages, as the catalog declares them. */
export const builtInSettings: readonly SettingEntry[] = Object.freeze((catalog.settings as RawSetting[]).map(raw => Object.freeze({
  id: raw.id, label: raw.label, help: raw.help ?? null, page: raw.page, section: raw.section ?? null, kind: raw.kind as SettingKind,
  choices: Object.freeze((raw.choices ?? []).map(choiceOf)), minimum: raw.minimum ?? null, maximum: raw.maximum ?? null, defaultValue: raw.default ?? null,
  owner: raw.owner ?? "host", agents: raw.agents ?? "read", writable: raw.writable !== false, managedBy: raw.managedBy ?? null, applies: raw.applies ?? "now",
  scopes: Object.freeze(raw.scopes ?? ["user"]), keywords: Object.freeze(raw.keywords ?? []), plugin: null, pluginName: null, available: true, builtIn: true,
})));

function parseDefault(text: string | null): unknown {
  if (text === null) return null;
  try { return JSON.parse(text) as unknown; } catch { return null; }
}

/** A setting a plugin declares, as the host describes it. */
function pluginEntry(descriptor: SettingsDescriptor): SettingEntry {
  return Object.freeze({
    id: descriptor.id, label: descriptor.label, help: descriptor.help ?? null, page: descriptor.page, section: descriptor.section ?? null, kind: descriptor.kind as SettingKind,
    choices: Object.freeze(descriptor.choices.map(choice => Object.freeze({ value: choice.value, label: choice.label ?? null, literal: true }))),
    minimum: descriptor.minimum ?? null, maximum: descriptor.maximum ?? null, defaultValue: parseDefault(descriptor.default ?? null), owner: descriptor.owner,
    agents: descriptor.agents, writable: descriptor.writable, managedBy: descriptor.managedBy ?? null, applies: descriptor.applies, scopes: Object.freeze([...descriptor.scopes]),
    keywords: Object.freeze([...descriptor.keywords]), plugin: descriptor.plugin ?? null, pluginName: descriptor.pluginName ?? null, available: descriptor.available, builtIn: false,
  });
}

/**
 * The settings the window shows: the built-in ones, unavailable where the host says so, then those of the plugins
 * the host lists. Without the host (null), the built-in ones alone.
 */
export function mergeSettings(host: readonly SettingsDescriptor[] | null): readonly SettingEntry[] {
  if (!host) return builtInSettings;
  const described = new Map(host.map(descriptor => [descriptor.id, descriptor]));
  const builtIn = builtInSettings.map(entry => described.get(entry.id)?.available === false ? Object.freeze({ ...entry, available: false }) : entry);
  const known = new Set(builtInSettings.map(entry => entry.id));
  return Object.freeze([...builtIn, ...host.filter(descriptor => !known.has(descriptor.id)).map(pluginEntry)]);
}

/** Whether a text is a key of the dictionaries of the window. */
export const isMessageKey = (text: string): text is MessageKey => Object.hasOwn(messages, text);

/** A text of a setting in the shown language: a built-in one translated, a plugin's as it is written. */
export function settingText(locale: string, text: string, builtIn: boolean): string {
  return builtIn && isMessageKey(text) ? translate(locale, text) : text;
}

/** The text of a choice in the shown language: its label translated, else its own text, else its value. */
export function choiceText(locale: string, choice: SettingChoice, builtIn: boolean): string {
  if (choice.label === null) return choice.value;
  return choice.literal ? choice.label : settingText(locale, choice.label, builtIn);
}
