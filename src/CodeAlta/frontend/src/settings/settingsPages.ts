import type { IconName } from "../AppIcon";
import type { MessageKey } from "../localization";
import catalog from "./builtInSettings.json";

/** A page of Settings with a fixed place in its navigation; spaces and plugins add pages of their own. */
export type SettingsPageId = "appearance" | "general" | "windows" | "speech" | "search" | "spaces" | "labels" | "providers" | "models" | "prompts" | "skills"
  | "permissions" | "sandbox" | "codeMode" | "sessions" | "worktrees" | "workItems" | "pullRequests" | "plugins" | "mcp" | "config" | "storage" | "mcpHost"
  | "logs" | "about";

/**
 * One fixed page of Settings: its id, its name and icon in the navigation, and English words that find it beside its
 * name and the settings it holds.
 */
export type SettingsPageEntry = Readonly<{ id: SettingsPageId; label: MessageKey; icon: IconName; keywords: readonly string[] }>;

type SettingsGroup = Readonly<{ group: MessageKey; pages: readonly SettingsPageEntry[] }>;

/**
 * The groups of the navigation of Settings, in order, with their pages, as the catalog of the settings declares them
 * (`builtInSettings.json`, which the host reads too). The Spaces group also lists every space after its own pages.
 */
export const settingsGroups: readonly SettingsGroup[] = Object.freeze(catalog.groups.map(group => Object.freeze({
  group: group.group as MessageKey,
  pages: Object.freeze(group.pages.map(page => Object.freeze({ ...page, id: page.id as SettingsPageId, label: page.label as MessageKey, icon: page.icon as IconName }))),
})));

/** Every fixed page, in the order of the navigation, with the name of its group. */
export const settingsPages: readonly (SettingsPageEntry & { group: MessageKey })[] = Object.freeze(
  settingsGroups.flatMap(({ group, pages }) => pages.map(page => Object.freeze({ ...page, group }))));

/** The fixed page of an id, or undefined. */
export function settingsPageOf(id: string): (SettingsPageEntry & { group: MessageKey }) | undefined {
  return settingsPages.find(page => page.id === id);
}
