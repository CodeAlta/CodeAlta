import { useContext } from "react";
import type { SessionMenuEntry } from "../SessionTabMenu";
import { PluginIcon } from "./PluginIcon";
import { useShownPluginButtons } from "./PluginButtons";
import { PluginButtonsContext, badgeText, type PluginButtonContext, type PluginButtonPlace, type PluginButtonView } from "./pluginButtonModel";

/** The line of a menu for a button of a plugin: its icon and label, its badge at the end, and what it does for the row. */
export function pluginMenuEntry(button: PluginButtonView, context: PluginButtonContext, activate: (button: PluginButtonView, context: PluginButtonContext) => void): SessionMenuEntry {
  const count = badgeText(button);
  return {
    key: `plugin:${button.id}`, label: button.label, disabled: button.disabled,
    iconNode: <PluginIcon icon={button.icon} data={button.iconData} pluginKey={button.pluginKey} size={15} />,
    endNode: count ? <span className="plugin-menu-badge" data-tone={button.tone}>{count}</span> : button.badge === "dot" ? <span className="plugin-menu-badge" data-kind="dot" data-tone={button.tone} /> : undefined,
    onSelect: () => activate(button, context),
  };
}

/**
 * The lines that plugins add to the menu of a project row or of a session row, read for that row: its project, and its
 * session, whatever is selected. A null context (no menu is open) reads nothing. The lines follow a divider.
 */
export function usePluginMenuEntries(place: Extract<PluginButtonPlace, "ProjectMenu" | "SessionMenu">, context: PluginButtonContext | null): SessionMenuEntry[] {
  const host = useContext(PluginButtonsContext);
  const buttons = useShownPluginButtons(place, context);
  if (!context || buttons.length === 0) return [];
  return [{ key: "plugins", divider: true }, ...buttons.map(button => pluginMenuEntry(button, context, host.activate))];
}
