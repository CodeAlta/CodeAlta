import { PluginIcon } from "../pluginButtons/PluginIcon";

/**
 * The icon of a canvas tab: the one the plugin named, drawn the way the icon of a button of a plugin is (a file of the
 * plugin, a Lucide icon, a logo of a brand), and a neutral plugin icon when there is none or it is not found.
 * `pluginKey` lets an icon file of the plugin be found.
 */
export function CanvasIcon({ name, pluginKey, size = 14 }: { name?: string | null; pluginKey?: string | null; size?: number }) {
  return <PluginIcon icon={name} pluginKey={pluginKey} size={size} />;
}
