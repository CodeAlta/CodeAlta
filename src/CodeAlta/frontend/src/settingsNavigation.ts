/** Where Settings opens: a page (an id of the catalog, `spaces/<id>/<section>` or `plugin/<key>/<canvas>`), and the setting that glows on it. */
export type SettingsTarget = Readonly<{ page: string; setting: string | null }>;

type Listener = (target: SettingsTarget) => void;
const listeners = new Set<Listener>();

/**
 * Lets a control anywhere in the window (a composer status item, a result of the search, a toast) ask the shell to open
 * a Settings page, and to make a setting of it glow. The shell decides whether the page exists; an unknown page opens
 * nothing.
 */
export const settingsNavigation = Object.freeze({
  open(target: string | SettingsTarget) {
    const value: SettingsTarget = typeof target === "string" ? { page: target, setting: null } : target;
    for (const listener of [...listeners]) listener(value);
  },
  subscribe(listener: Listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
});
