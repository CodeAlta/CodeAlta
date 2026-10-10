import { createContext } from "react";
import type { pluginUi } from "#neoastra";

/** Where the window has room for a button of a plugin. */
export type PluginButtonPlace = "TitleBar" | "Rail" | "ProjectMenu" | "SessionMenu";
export const pluginButtonPlaces: readonly PluginButtonPlace[] = ["TitleBar", "Rail", "ProjectMenu", "SessionMenu"];
export type PluginButtonTone = "Info" | "Success" | "Warning" | "Error" | "Muted";
const tones: readonly string[] = ["Info", "Success", "Warning", "Error", "Muted"];
export type PluginButtonBadge = "none" | "count" | "dot" | "busy";
const badges: readonly string[] = ["none", "count", "dot", "busy"];

/** A button of a plugin, as the host listed it for a context. */
export type PluginButtonView = Readonly<{
  id: string; pluginKey: string; pluginId: string; plugin: string; buttonId: string; place: PluginButtonPlace;
  icon: string; iconData: string | null; label: string;
  /** The command it runs, as `pluginUi.invokeCommand` names it; null for a button that opens a canvas. */
  commandId: string | null;
  /** The canvas it opens and what that canvas is about; null for a button that runs a command. */
  canvas: string | null; canvasScope: "Application" | "Project" | "Session" | null;
  badge: PluginButtonBadge; count: number; tone: PluginButtonTone; hidden: boolean; disabled: boolean; tooltip: string | null;
}>;

/** The project and session a button is asked about and acts for: the selected ones, or the ones of a row. */
export type PluginButtonContext = Readonly<{ projectId: string | null; sessionId: string | null }>;

/** What the window gives the places that draw buttons of plugins. */
export type PluginButtonsHost = Readonly<{
  /** The host epoch; null when the window has no host of its own, which draws no button. */
  epoch: string | null;
  /** The space the window shows. */
  spaceId: string | null;
  api: Pick<typeof pluginUi, "buttons">;
  /** Does what a button does, for the context: runs its command or opens its canvas. */
  activate: (button: PluginButtonView, context: PluginButtonContext) => void;
  /** Whether the tab in front is the canvas that a button opens in a context: the button is then marked. */
  isActive: (button: PluginButtonView, context: PluginButtonContext) => boolean;
  /** The buttons the user hid in this window. */
  hidden: HiddenButtons;
}>;
const inert: PluginButtonsHost = Object.freeze({
  epoch: null, spaceId: null, api: { buttons: async () => ({ status: "unavailable", place: null, buttons: [] }) }, activate: () => { }, isActive: () => false, hidden: createHiddenButtons(null),
});
export const PluginButtonsContext = createContext<PluginButtonsHost>(inert);
/**
 * What decides which button is marked as active (the tab in front). Only the places that draw marks read it, so a change of tab redraws them and not
 * every row of the Explorer that asks for the lines of its menu.
 */
export const PluginButtonsActiveContext = createContext<unknown>(null);

/** Raised on the window when the host says that what the buttons of plugins show may have changed. */
export const pluginButtonsChangedEvent = "codealta:plugin-buttons-changed";

const line = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length <= maximum && !/[\u0000-\u001f\u007f]/u.test(value);

/** Accepts a well-formed `pluginUi.buttons` answer; anything else is null. A button that is not well formed is left out. */
export function readPluginButtons(reply: unknown): PluginButtonView[] | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok" || !Array.isArray(value.buttons)) return null;
  const buttons: PluginButtonView[] = [];
  for (const entry of value.buttons.slice(0, 256)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    if (!line(item.id, 512) || !item.id || !line(item.pluginKey, 512) || !item.pluginKey || !line(item.pluginId, 512) || !line(item.plugin, 200)
      || !line(item.buttonId, 64) || !/^[A-Za-z0-9._-]+$/u.test(item.buttonId) || !pluginButtonPlaces.includes(item.place as PluginButtonPlace)
      || !line(item.icon, 200) || !item.icon || !line(item.label, 200) || !item.label || buttons.some(known => known.id === item.id)) continue;
    const commandId = line(item.commandId, 512) && item.commandId ? item.commandId : null;
    const canvas = line(item.canvas, 64) && item.canvas ? item.canvas : null;
    // Exactly one of a command and a canvas.
    if (!commandId === !canvas) continue;
    const scope = item.canvasScope === "Application" || item.canvasScope === "Project" || item.canvasScope === "Session" ? item.canvasScope : null;
    if (canvas && !scope) continue;
    const badge = badges.includes(item.badge as string) ? item.badge as PluginButtonBadge : "none";
    buttons.push({
      id: item.id, pluginKey: item.pluginKey, pluginId: item.pluginId as string, plugin: item.plugin as string, buttonId: item.buttonId, place: item.place as PluginButtonPlace,
      icon: item.icon, iconData: typeof item.iconData === "string" && item.iconData.startsWith("data:image/svg+xml;base64,") && item.iconData.length <= 64 * 1024 ? item.iconData : null,
      label: item.label, commandId, canvas, canvasScope: canvas ? scope : null,
      badge, count: badge === "count" && Number.isSafeInteger(item.count) && (item.count as number) > 0 ? item.count as number : 0,
      tone: tones.includes(item.tone as string) ? item.tone as PluginButtonTone : "Info",
      hidden: item.hidden === true, disabled: item.disabled === true, tooltip: line(item.tooltip, 1024) && item.tooltip ? item.tooltip : null,
    });
  }
  return buttons;
}

/** Whether two lists show the same thing, so a read that changed nothing keeps what is drawn. */
export function samePluginButtons(left: readonly PluginButtonView[], right: readonly PluginButtonView[]): boolean {
  return left.length === right.length && left.every((item, index) => {
    const other = right[index];
    return item.id === other.id && item.icon === other.icon && item.iconData === other.iconData && item.label === other.label && item.commandId === other.commandId
      && item.canvas === other.canvas && item.badge === other.badge && item.count === other.count && item.tone === other.tone && item.hidden === other.hidden
      && item.disabled === other.disabled && item.tooltip === other.tooltip;
  });
}

/** The number a badge shows: 99+ beyond two digits. */
export function badgeText(button: Pick<PluginButtonView, "badge" | "count">): string | null {
  return button.badge === "count" && button.count > 0 ? button.count > 99 ? "99+" : String(button.count) : null;
}

/** The name of a button for a screen reader: its label, with the number its badge shows. */
export function buttonName(button: PluginButtonView, withCount: (label: string, count: string) => string): string {
  const text = badgeText(button);
  return text ? withCount(button.label, text) : button.label;
}

/** The tooltip of a button: what the plugin said, or its label with the number of its badge. */
export function buttonTooltip(button: PluginButtonView, withCount: (label: string, count: string) => string): string {
  return button.tooltip ?? buttonName(button, withCount);
}

/** The key the user's choice to hide a button is kept under: the plugin and the button, not the space. */
export const hiddenKey = (button: Readonly<{ pluginKey: string; buttonId: string }>) => `${button.pluginKey}\n${button.buttonId}`;

export const hiddenButtonsStorageKey = "codealta.desktop.pluginButtons.hidden.v1";
const maximumHidden = 512;

/** Reads the hidden buttons as they were stored; what is not a list of keys is nothing hidden. */
export function readHiddenButtons(read: () => string | null): ReadonlySet<string> {
  try {
    const value: unknown = JSON.parse(read() ?? "[]");
    return new Set(Array.isArray(value) ? value.filter((key): key is string => typeof key === "string" && key.length <= 600 && key.includes("\n")).slice(0, maximumHidden) : []);
  } catch { return new Set(); }
}

/** The buttons the user hid, kept in the window: a view state, like the open tabs. */
export type HiddenButtons = Readonly<{
  isHidden: (button: Readonly<{ pluginKey: string; buttonId: string }>) => boolean;
  set: (button: Readonly<{ pluginKey: string; buttonId: string }>, hidden: boolean) => void;
  getSnapshot: () => ReadonlySet<string>;
  subscribe: (listener: () => void) => () => void;
}>;

/** Creates the store of hidden buttons over a storage; a storage that cannot be written still holds the choice until the window closes. */
export function createHiddenButtons(storage: Pick<Storage, "getItem" | "setItem"> | null): HiddenButtons {
  let hidden = readHiddenButtons(() => { try { return storage?.getItem(hiddenButtonsStorageKey) ?? null; } catch { return null; } });
  const listeners = new Set<() => void>();
  const notify = () => listeners.forEach(listener => listener());
  return {
    isHidden: button => hidden.has(hiddenKey(button)),
    set(button, value) {
      const key = hiddenKey(button);
      if (hidden.has(key) === value) return;
      const next = new Set(hidden);
      if (value) next.add(key); else next.delete(key);
      hidden = next;
      try { storage?.setItem(hiddenButtonsStorageKey, JSON.stringify([...hidden].slice(-maximumHidden))); } catch { /* The choice holds until the window closes. */ }
      notify();
    },
    getSnapshot: () => hidden,
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  };
}

/** The buttons to draw: the ones the plugin does not hide for now and the user did not hide. */
export function shownButtons(buttons: readonly PluginButtonView[], hidden: ReadonlySet<string>): PluginButtonView[] {
  return buttons.filter(button => !button.hidden && !hidden.has(hiddenKey(button)));
}

/**
 * Splits buttons into the ones drawn in place and the ones folded into the overflow menu. When the window is narrow
 * they all fold, so that nothing of the application moves before the buttons of plugins do; otherwise the ones past
 * `inline` fold.
 */
export function foldButtons(buttons: readonly PluginButtonView[], inline: number, narrow: boolean): { inline: PluginButtonView[]; folded: PluginButtonView[] } {
  if (narrow) return { inline: [], folded: [...buttons] };
  return buttons.length <= inline ? { inline: [...buttons], folded: [] } : { inline: buttons.slice(0, inline), folded: buttons.slice(inline) };
}

/** The window is narrow when it is no wider than this: the space switch folds its name below 875, and the buttons of plugins fold before it. */
export const narrowWindowWidth = 1100;
/** The most buttons the rail draws before it folds the rest into a menu. */
export const railInlineLimit = 2;
