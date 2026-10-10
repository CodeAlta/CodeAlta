import { useContext } from "react";
import { PluginIcon } from "../pluginButtons/PluginIcon";
import { AltaReactContext } from "./PluginScript";

/**
 * An icon of the window by its name, as a button of a plugin names its own: one of the general icons, any other icon of the icon library by
 * its kebab-case name (`chart-column`; the library loads when a script first names an icon the application does not draw itself), a file
 * of the plugin package (`icons/star.svg`, drawn in the color of the text), or the logo of a brand; a neutral icon when the name is none of
 * them. This is the one place scripts ask for an icon by name: what widens the icons of buttons and tabs reaches scripts through it.
 */
export function Icon({ name, size = 14, title, className }: { name: string; size?: number; title?: string; className?: string }) {
  const alta = useContext(AltaReactContext);
  const icon = <PluginIcon icon={name} pluginKey={alta?.context.pluginKey ?? null} size={size} className={className} />;
  return title ? <span className="plugin-icon-titled" title={title} role="img" aria-label={title}>{icon}</span> : icon;
}
