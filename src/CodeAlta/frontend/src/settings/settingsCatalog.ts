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
  /** The project of a plugin of a project, whose settings are all that project's; null otherwise. */
  project: string | null;
  /**
   * Built in, or of a plugin that ships with CodeAlta: its texts are keys of the dictionaries of the window where they
   * have a translation, and shown as they are written otherwise.
   */
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
  scopes: Object.freeze(raw.scopes ?? ["user"]), keywords: Object.freeze(raw.keywords ?? []), plugin: null, pluginName: null, available: true, project: null, builtIn: true,
})));

// A bound of a number, written as a text by the host: a bound of a plugin may be a fraction.
function bound(text: string | null | undefined): number | null {
  if (text === null || text === undefined || text.trim() === "") return null;
  const value = Number(text);
  return Number.isFinite(value) ? value : null;
}

function parseDefault(text: string | null): unknown {
  if (text === null) return null;
  try { return JSON.parse(text) as unknown; } catch { return null; }
}

/** A setting a plugin declares, as the host describes it. */
function pluginEntry(descriptor: SettingsDescriptor): SettingEntry {
  // A plugin that ships with CodeAlta has its page under its built-in key: its texts are translated where they can be.
  const shipped = descriptor.page.startsWith("plugin/builtin:");
  return Object.freeze({
    id: descriptor.id, label: descriptor.label, help: descriptor.help ?? null, page: descriptor.page, section: descriptor.section ?? null, kind: descriptor.kind as SettingKind,
    choices: Object.freeze(descriptor.choices.map(choice => Object.freeze({ value: choice.value, label: choice.label ?? null, literal: !shipped }))),
    minimum: bound(descriptor.minimum), maximum: bound(descriptor.maximum), defaultValue: parseDefault(descriptor.default ?? null), owner: descriptor.owner,
    agents: descriptor.agents, writable: descriptor.writable, managedBy: descriptor.managedBy ?? null, applies: descriptor.applies, scopes: Object.freeze([...descriptor.scopes]),
    keywords: Object.freeze([...descriptor.keywords]), plugin: descriptor.plugin ?? null, pluginName: descriptor.pluginName ?? null, available: descriptor.available,
    project: descriptor.project ?? null, builtIn: shipped,
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

/** A text of a setting in the shown language: a built-in one translated where it can be, a plugin's as it is written. */
export function settingText(locale: string, text: string, builtIn: boolean): string {
  return builtIn && isMessageKey(text) ? translate(locale, text) : text;
}

/** Whether the key of a plugin is the one of a plugin of the application, whose texts the window translates. */
export const isBuiltInPluginKey = (pluginKey: string | null | undefined) => !!pluginKey?.startsWith("builtin:");

const htmlEntities: Readonly<Record<string, string>> = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": "\"", "&#39;": "'" };
// The elements whose text is data, never a text of the plugin: a value a user wrote is shown as it is.
const dataElements = new Set(["td", "code", "pre", "textarea", "kbd", "samp"]);

/**
 * The HTML of a plugin in the shown language: of a built-in plugin, each text between two tags that is a key of the
 * dictionaries is translated, except in the cells of a table and in code; a plugin's as it is written.
 */
export function pluginHtmlText(locale: string, html: string, builtIn: boolean): string {
  if (!builtIn) return html;
  return html.replace(/<(\/?)([a-zA-Z][\w-]*)[^>]*>([^<]+)/g, (match, closing: string, tag: string, text: string) => {
    const trimmed = text.trim();
    if (!trimmed || (!closing && dataElements.has(tag.toLowerCase()))) return match;
    const key = trimmed.replace(/&(?:amp|lt|gt|quot|#39);/g, entity => htmlEntities[entity]);
    if (!isMessageKey(key)) return match;
    const translated = translate(locale, key).replace(/[&<>]/g, character => character === "&" ? "&amp;" : character === "<" ? "&lt;" : "&gt;");
    return match.slice(0, match.length - text.length) + text.replace(trimmed, () => translated);
  });
}

/**
 * A short text of a built-in plugin in the shown language, such as a status ("31 values hidden", "3/3 · tools not
 * loaded"): each part between two " · " is translated when, its numbers made parameters ("{0} values hidden"), it is a
 * key of the dictionaries; a plugin's as it is written.
 */
export function pluginShortText(locale: string, text: string, builtIn: boolean): string {
  if (!builtIn) return text;
  return text.split(" · ").map(part => {
    const numbers: string[] = [];
    const key = part.replace(/\d+(?:[.,]\d+)?/g, number => `{${numbers.push(number) - 1}}`);
    return isMessageKey(key) ? translate(locale, key, Object.fromEntries(numbers.map((number, index) => [String(index), number]))) : part;
  }).join(" · ");
}

/** The text of a choice in the shown language: its label translated, else its own text, else its value. */
export function choiceText(locale: string, choice: SettingChoice, builtIn: boolean): string {
  if (choice.label === null) return choice.value;
  return choice.literal ? choice.label : settingText(locale, choice.label, builtIn);
}
