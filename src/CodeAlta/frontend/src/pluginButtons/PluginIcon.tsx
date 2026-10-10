import { createContext, useContext, useEffect, useState, useSyncExternalStore } from "react";
import { AppIcon } from "../AppIcon";
import { BrandIcon, isBrandIcon } from "../BrandIcon";
import { isSymbolIcon, symbolIcons } from "../symbolIcons";
import { createIconLoader, isIconFile, pluginIconFiles, type LibraryIcon } from "./pluginIcons";

/** The loader of the icon library: its chunk comes when a plugin first names an icon the application does not draw itself. */
const libraryLoader = createIconLoader(async () => (await import("./lucideIcons")).icons as Readonly<Record<string, unknown>>);
/** Where the icons of plugin files and of the library are found: the page's own, unless a test brings another. */
export const PluginIconSources = createContext({ loader: libraryLoader, files: pluginIconFiles });

/** The data of the icon file a plugin named, or null while the host has sent none. */
export function usePluginIconFile(pluginKey: string | null | undefined, icon: string | null | undefined): string | null {
  const { files } = useContext(PluginIconSources);
  useSyncExternalStore(files.subscribe, files.getVersion, files.getVersion);
  return files.get(pluginKey, icon);
}

/**
 * The icon a plugin chose, in the size and the color of the text around it: a file of the plugin (drawn as a mask, so that
 * it follows the theme), an icon the application draws itself, any other icon of the Lucide library (its chunk loads on
 * the first use), or a logo of a brand, in that order of looking. A name that is none of them draws a neutral icon: an
 * icon that does not exist never breaks what is around it.
 *
 * `data` is the file of the plugin when the host sent it with the button; a canvas gives `pluginKey` and the host's
 * registry finds it.
 */
export function PluginIcon({ icon, data, pluginKey, size = 16, className }: { icon: string | null | undefined; data?: string | null; pluginKey?: string | null; size?: number; className?: string }) {
  const { loader } = useContext(PluginIconSources);
  const registered = usePluginIconFile(pluginKey, icon);
  const file = data ?? registered;
  const name = icon ?? "";
  const symbolic = isSymbolIcon(name);
  const wantsFile = !!name && isIconFile(name);
  // `undefined` while the library loads, `null` when it has no such icon.
  const [library, setLibrary] = useState<{ name: string; icon: LibraryIcon | null } | undefined>(() => {
    const known = !symbolic && !wantsFile && name ? loader.peek(name) : undefined;
    return known === undefined ? undefined : { name, icon: known };
  });
  useEffect(() => {
    if (symbolic || wantsFile || !name) return;
    let current = true;
    void loader.resolve(name).then(found => { if (current) setLibrary({ name, icon: found }); });
    return () => { current = false; };
  }, [loader, name, symbolic, wantsFile]);

  const box = { width: size, height: size };
  if (file) {
    return <span className={className ? `plugin-icon plugin-icon-file ${className}` : "plugin-icon plugin-icon-file"} aria-hidden="true"
      style={{ ...box, maskImage: `url("${file}")`, WebkitMaskImage: `url("${file}")` }} />;
  }
  if (symbolic) {
    const Symbol = symbolIcons[name];
    return <Symbol size={size} aria-hidden="true" className={className} />;
  }
  if (!wantsFile && name && library?.name === name && library.icon) {
    const Library = library.icon;
    return <Library size={size} aria-hidden="true" className={className} />;
  }
  // The library has no such icon: a logo of a brand when there is one.
  if (!wantsFile && library?.name === name && isBrandIcon(name)) return <BrandIcon name={name} size={size} className={className} />;
  // While the library loads the place is kept, so that nothing moves when the icon arrives.
  if (!wantsFile && name && library?.name !== name) return <span className={className ? `plugin-icon plugin-icon-pending ${className}` : "plugin-icon plugin-icon-pending"} aria-hidden="true" style={box} />;
  return <AppIcon name="plugin" size={size} aria-hidden="true" className={className} />;
}
