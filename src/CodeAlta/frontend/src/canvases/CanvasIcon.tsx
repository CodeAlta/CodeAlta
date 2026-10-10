import { AppIcon } from "../AppIcon";
import { isSymbolIcon, symbolIcons } from "../symbolIcons";

/**
 * The icon of a canvas tab: the one the plugin named, when it is among the general icons of the window, and the
 * icon of a plugin otherwise.
 */
export function CanvasIcon({ name, size = 14 }: { name?: string | null; size?: number }) {
  if (isSymbolIcon(name)) {
    const Icon = symbolIcons[name];
    return <Icon size={size} aria-hidden="true" />;
  }
  return <AppIcon name="plugin" size={size} aria-hidden="true" />;
}
