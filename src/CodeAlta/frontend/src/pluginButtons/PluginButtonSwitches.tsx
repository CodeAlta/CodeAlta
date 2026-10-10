import { useContext, useId, useSyncExternalStore } from "react";
import { Switch, Tag } from "@blueprintjs/core";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { PluginIcon } from "./PluginIcon";
import { PluginButtonsContext, hiddenKey, type PluginButtonPlace, type PluginButtonView } from "./pluginButtonModel";

const placeNames: Readonly<Record<PluginButtonPlace, MessageKey>> = {
  TitleBar: "Title bar", Rail: "Navigation rail", ProjectMenu: "Project menu", SessionMenu: "Session menu",
};

/**
 * The buttons of one plugin, each with the switch that shows or hides it in this window (a view state kept like the open
 * tabs). A button the plugin hides for now is still listed: the switch is the user's choice, not the plugin's.
 */
export function PluginButtonSwitches({ buttons }: { buttons: readonly PluginButtonView[] }) {
  const { t } = useShellLanguage();
  const host = useContext(PluginButtonsContext);
  const id = useId();
  const hidden = useSyncExternalStore(host.hidden.subscribe, host.hidden.getSnapshot, host.hidden.getSnapshot);
  if (buttons.length === 0) return null;
  return <ul className="plugin-button-list" aria-label={t("Plugin buttons")}>
    {buttons.map(button => <li key={button.id}>
      <PluginIcon icon={button.icon} data={button.iconData} pluginKey={button.pluginKey} size={15} />
      <span className="plugin-button-name">{button.label}</span>
      <Tag minimal round id={`${id}-${button.id}-${button.place}`}>{t(placeNames[button.place])}</Tag>
      {/* Two buttons of a plugin can have the same label at two places: the place tells them apart. */}
      <Switch checked={!hidden.has(hiddenKey(button))} aria-label={t("Show the {label} button", { label: button.label })} aria-describedby={`${id}-${button.id}-${button.place}`}
        onChange={event => host.hidden.set(button, !event.currentTarget.checked)} />
    </li>)}
  </ul>;
}
