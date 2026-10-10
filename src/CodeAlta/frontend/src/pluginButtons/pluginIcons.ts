import type { ComponentType } from "react";

/** An icon of the library, as the page draws it. */
export type LibraryIcon = ComponentType<{ size?: number | string; "aria-hidden"?: boolean | "true" | "false"; className?: string }>;

const iconName = /^[a-z0-9]+(?:-[a-z0-9]+)*$/u;

/**
 * The name an icon has in the library's exports: `chart-column` is `ChartColumn`, `building-2` is `Building2`. Null for
 * a text that is not the kebab-case name of an icon.
 */
export function libraryExportName(name: string): string | null {
  if (name.length > 64 || !iconName.test(name)) return null;
  return name.split("-").map(part => part[0].toUpperCase() + part.slice(1)).join("");
}

/** Whether an icon names a file of the plugin rather than an icon of the libraries: a path, or a name that ends in `.svg`. */
export function isIconFile(icon: string): boolean {
  return /\.svg$/iu.test(icon) || icon.includes("/") || icon.includes("\\");
}

/**
 * Finds icons of the library by name, loading the library once, the first time a name is not one of the icons the
 * application draws itself. What was asked is answered from memory afterwards.
 */
export function createIconLoader(load: () => Promise<Readonly<Record<string, unknown>>>) {
  let library: Readonly<Record<string, unknown>> | null = null;
  let loading: Promise<Readonly<Record<string, unknown>> | null> | null = null;
  const find = (name: string): LibraryIcon | null => {
    const exported = libraryExportName(name);
    // An icon is a component: a function, or an object that React renders (forwardRef).
    const found = exported && library && Object.hasOwn(library, exported) ? library[exported] : null;
    return found && (typeof found === "function" || typeof found === "object") ? found as LibraryIcon : null;
  };
  return {
    /** The icon when the library is loaded: the icon, or null when the library has no such icon. Undefined while the library is not loaded. */
    peek(name: string): LibraryIcon | null | undefined { return library ? find(name) : undefined; },
    /** Loads the library if it is not, and finds the icon: null when there is none, or when the library cannot be loaded. */
    async resolve(name: string): Promise<LibraryIcon | null> {
      if (!libraryExportName(name)) return null;
      if (!library) {
        loading ??= load().then(value => { library = value; return value; }, () => { loading = null; return null; });
        if (!await loading) return null;
      }
      return find(name);
    },
  };
}

/** The icons of plugin files that the host sent (clean SVG data URLs), by plugin and path, and who wants to know when one arrives. */
export function createIconFiles() {
  const files = new Map<string, string>();
  const listeners = new Set<() => void>();
  let version = 0;
  const key = (pluginKey: string, icon: string) => `${pluginKey}\n${icon}`;
  return {
    /** Keeps the data of an icon file, or forgets it when the host sent none. */
    register(pluginKey: string, icon: string | null | undefined, data: string | null | undefined) {
      if (!icon) return;
      const name = key(pluginKey, icon);
      if (data) { if (files.get(name) === data) return; files.set(name, data); }
      else if (!files.delete(name)) return;
      version++;
      listeners.forEach(listener => listener());
    },
    get(pluginKey: string | null | undefined, icon: string | null | undefined): string | null { return pluginKey && icon ? files.get(key(pluginKey, icon)) ?? null : null; },
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getVersion: () => version,
  };
}
export type IconFiles = ReturnType<typeof createIconFiles>;

/** The icon files of plugins that the host sent with what lists them: the one of the window. */
export const pluginIconFiles: IconFiles = createIconFiles();
