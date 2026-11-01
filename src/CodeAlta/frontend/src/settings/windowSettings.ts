import { useEffect, useMemo, useRef } from "react";
import type { SettingsEvent } from "#neoastra";
import { useChangesPreferences } from "../changes/changesPreferences";
import { colorSchemes, customSchemeSelection, type CustomColorScheme } from "../colorSchemes";
import type { ProjectSort } from "../explorer/projectRail";
import { useFontPreferences } from "../fonts/fontPreferences";
import { landingPreferences, useLandingPreferences } from "../landing/landingPreferences";
import { validLanguage } from "../localization";
import { maximumModelPickerRows, minimumModelPickerRows, modelPickerPreferences, useModelPickerRows } from "../modelPicker";
import { useTimelineEmphasis } from "../timelineEmphasis/useTimelineEmphasis";
import { themes, type Confirmation, type Theme } from "../windowPreferences";
import type { SettingsHub } from "./settingsHub";
import { writeExpired } from "./settingValues";

/** The settings the page keeps: those `builtInSettings.json` declares with the owner `window`. */
export const windowSettingIds = Object.freeze(["appearance.theme", "appearance.darker", "appearance.color_scheme", "appearance.code_font", "appearance.prompt_font",
  "appearance.project_sort", "appearance.recent_sessions", "appearance.sub_agents", "appearance.collapse_rail", "appearance.focused_timeline", "appearance.folded_images",
  "appearance.entry_appearance", "appearance.changes_view", "general.language", "general.welcome_at_startup", "general.animate_welcome",
  "permissions.confirm_session_delete", "permissions.confirm_project_archive", "permissions.confirm_plugin_stop", "sessions.single_tab", "models.picker_rows",
  "speech.microphone"] as const);
export type WindowSettingId = typeof windowSettingIds[number];

/** A setting the page keeps: its value now, what changes it (returns why it refused, or null), and its choices when they change. */
export type WindowSettingBinding = Readonly<{ value: unknown; set(value: unknown): string | null; choices?: readonly { value: string; label: string | null }[] }>;

/** The preferences of the window that its settings are bound to: what `useWindowPreferences` and the language give. */
export type WindowSettingSources = Readonly<{
  theme: Theme; setTheme(value: Theme): void;
  darker: boolean; setDarker(value: boolean): void;
  colorScheme: string; setColorScheme(value: string): void; customSchemes: readonly CustomColorScheme[] | null;
  projectSort: ProjectSort; setProjectSort(value: ProjectSort): void;
  recentSessionCount: number; setRecentSessionCount(value: number): void;
  subAgentCount: number; setSubAgentCount(value: number): void;
  railCollapsed: boolean; setRailCollapsed(value: boolean): void;
  confirms: Readonly<Record<Confirmation, boolean>>; setConfirm(confirmation: Confirmation, ask: boolean): void;
  singleTab: boolean; setSingleTab(value: boolean): void;
  language: string; setLanguage(value: string): void;
}>;

const integer = (value: unknown, minimum: number, maximum: number) => typeof value === "number" && Number.isInteger(value) && value >= minimum && value <= maximum;
const bool = (set: (value: boolean) => void) => (value: unknown) => typeof value === "boolean" ? (set(value), null) : "The value is true or false.";

/**
 * The settings the page keeps, by id, over the preferences of the window: what `builtInSettings.json` declares with the
 * owner `window`. Each one changes through the setter its control on a page uses, so a change made by an agent or by the
 * search of Settings is the same as one made on the page.
 */
export function useWindowSettingBindings(sources: WindowSettingSources): ReadonlyMap<string, WindowSettingBinding> {
  const [fonts, setFonts] = useFontPreferences();
  const [changes, setChanges] = useChangesPreferences();
  const [emphasis, setEmphasis] = useTimelineEmphasis();
  const landing = useLandingPreferences();
  const pickerRows = useModelPickerRows();
  const latest = useRef(sources); latest.current = sources;
  const { theme, darker, colorScheme, customSchemes, projectSort, recentSessionCount, subAgentCount, railCollapsed, confirms, singleTab, language } = sources;
  return useMemo(() => {
    const now = () => latest.current;
    const schemeChoices = [...colorSchemes.map(scheme => ({ value: scheme.id, label: scheme.name })),
      ...(customSchemes ?? []).map(scheme => ({ value: customSchemeSelection(scheme.id), label: scheme.name }))];
    // Every setting the page keeps has its binding here: the type names them all.
    const entries: Record<Exclude<WindowSettingId, "speech.microphone">, WindowSettingBinding> = {
      "appearance.theme": { value: theme, set: value => themes.includes(value as Theme) ? (now().setTheme(value as Theme), null) : "The theme is dark, light or system." },
      "appearance.darker": { value: darker, set: bool(value => now().setDarker(value)) },
      "appearance.color_scheme": { value: colorScheme, choices: schemeChoices,
        set: value => typeof value === "string" && schemeChoices.some(choice => choice.value === value) ? (now().setColorScheme(value), null) : "Unknown color scheme." },
      "appearance.code_font": { value: [...fonts.code],
        set: value => Array.isArray(value) && value.every(item => typeof item === "string") ? (setFonts({ code: value.map(item => item.trim()).filter(Boolean) }), null) : "The code font is a list of font names." },
      "appearance.prompt_font": { value: fonts.prompt, set: value => value === "code" || value === "interface" ? (setFonts({ prompt: value }), null) : "The prompt font is code or interface." },
      "appearance.project_sort": { value: projectSort, set: value => value === "name" || value === "recent" ? (now().setProjectSort(value), null) : "Projects sort by name or recent." },
      "appearance.recent_sessions": { value: recentSessionCount, set: value => integer(value, 1, 50) ? (now().setRecentSessionCount(value as number), null) : "A count from 1 to 50." },
      "appearance.sub_agents": { value: subAgentCount, set: value => integer(value, 1, 50) ? (now().setSubAgentCount(value as number), null) : "A count from 1 to 50." },
      "appearance.collapse_rail": { value: railCollapsed, set: bool(value => now().setRailCollapsed(value)) },
      "appearance.focused_timeline": { value: emphasis.focused, set: bool(value => setEmphasis({ ...emphasis, focused: value })) },
      "appearance.folded_images": { value: emphasis.foldedImages, set: bool(value => setEmphasis({ ...emphasis, foldedImages: value })) },
      "appearance.entry_appearance": { value: { enabled: emphasis.enabled, types: emphasis.types }, set: () => "The appearance of the entries is changed on its page." },
      "appearance.changes_view": { value: changes.view, set: value => value === "file" || value === "all" ? (setChanges({ view: value }), null) : "Changes are shown file by file (file) or all at once (all)." },
      "general.language": { value: language, set: value => validLanguage(value) ? (now().setLanguage(value), null) : "Unknown language." },
      "general.welcome_at_startup": { value: landing.openAtStartup, set: bool(value => landingPreferences.set("openAtStartup", value)) },
      "general.animate_welcome": { value: landing.animate, set: bool(value => landingPreferences.set("animate", value)) },
      "permissions.confirm_session_delete": { value: confirms.sessionDelete, set: bool(value => now().setConfirm("sessionDelete", value)) },
      "permissions.confirm_project_archive": { value: confirms.projectArchive, set: bool(value => now().setConfirm("projectArchive", value)) },
      "permissions.confirm_plugin_stop": { value: confirms.pluginStop, set: bool(value => now().setConfirm("pluginStop", value)) },
      "sessions.single_tab": { value: singleTab, set: bool(value => now().setSingleTab(value)) },
      "models.picker_rows": { value: pickerRows,
        set: value => integer(value, minimumModelPickerRows, maximumModelPickerRows) ? (modelPickerPreferences.set(value as number), null) : `A count from ${minimumModelPickerRows} to ${maximumModelPickerRows}.` },
    };
    return new Map(Object.entries(entries));
  }, [theme, darker, colorScheme, customSchemes, fonts, setFonts, projectSort, recentSessionCount, subAgentCount, railCollapsed, emphasis, setEmphasis, changes, setChanges,
    landing, confirms, singleTab, language, pickerRows]);
}

/**
 * Tells the host the values of the settings the page keeps, at once and whenever one changes, and changes one when the
 * host asks (an agent ran `alta settings set`), answering with the new value. Only the page of the main window does.
 */
export function useWindowSettingsBridge(hub: SettingsHub | null, connected: boolean, bindings: ReadonlyMap<string, WindowSettingBinding>) {
  const reported = useRef(new Map<string, string>());
  const latest = useRef(bindings); latest.current = bindings;
  useEffect(() => { reported.current = new Map(); }, [hub, connected]);
  useEffect(() => {
    if (!hub || !connected) return;
    const changed: { id: string; value: unknown; choices?: readonly { value: string; label: string | null }[] | null }[] = [];
    for (const [id, binding] of bindings) {
      const text = JSON.stringify([binding.value, binding.choices ?? null]);
      if (reported.current.get(id) === text) continue;
      reported.current.set(id, text);
      changed.push({ id, value: binding.value, choices: binding.choices ?? null });
    }
    if (changed.length) void hub.report(changed);
  }, [hub, connected, bindings]);
  useEffect(() => {
    if (!hub || !connected) return;
    return hub.onEvent((event: SettingsEvent) => {
      if (event.kind !== "write" || !event.id || !event.requestId) return;
      // A change that comes after the host stopped waiting is not made: the host told its caller it was not.
      if (writeExpired(event.expires, Date.now())) { void hub.report([], event.requestId, "The change came too late and was not made."); return; }
      const binding = latest.current.get(event.id);
      let error: string | null = binding ? null : "The window does not keep this setting.";
      let value: unknown = null;
      if (binding) {
        try { value = JSON.parse(event.value ?? "null") as unknown; } catch { error = "The value is not JSON."; }
        if (!error) error = binding.set(value);
      }
      // The answer carries the value the page took; the binding reports it again once the page shows it.
      void hub.report(error ? [] : [{ id: event.id, value }], event.requestId, error);
    });
  }, [hub, connected]);
}