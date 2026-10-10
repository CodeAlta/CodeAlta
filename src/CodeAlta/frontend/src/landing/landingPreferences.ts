import { useSyncExternalStore } from "react";

/** Whether the landing page opens when the application starts: `on` or `off`. */
export const landingStartupKey = "codealta.desktop.landing.startup.v1";
/** Whether the accent of the landing page moves: `on` or `off`. */
export const landingAnimationKey = "codealta.desktop.landing.animation.v1";

/** What the user chose for the landing page. It is kept by the window, on this computer. */
export type LandingPreferences = Readonly<{
  /** The page opens in front when the application starts. A tab that was left open is restored either way. */
  openAtStartup: boolean;
  /** The accent of the page moves. It never moves for someone who asked the system for less motion. */
  animate: boolean;
}>;

/** A new profile is welcomed by the page; the switch on the page turns it off. */
export const defaultLandingPreferences: LandingPreferences = Object.freeze({ openAtStartup: true, animate: true });

type Store = Pick<Storage, "getItem" | "setItem">;
const keys: Readonly<Record<keyof LandingPreferences, string>> = { openAtStartup: landingStartupKey, animate: landingAnimationKey };

function read(storage: Store | null, name: keyof LandingPreferences): boolean {
  try {
    const value = storage?.getItem(keys[name]);
    return value === "on" ? true : value === "off" ? false : defaultLandingPreferences[name];
  } catch { return defaultLandingPreferences[name]; }
}

/**
 * The preferences of the landing page over a storage: read once, changed by `set`, and told to whoever listens, so the switch of the
 * page and the one of Settings show the same thing. A storage that cannot be read gives the defaults, and one that cannot be
 * written keeps the choice until the window closes.
 */
export function createLandingPreferences(storage: Store | null) {
  let current: LandingPreferences = Object.freeze({ openAtStartup: read(storage, "openAtStartup"), animate: read(storage, "animate") });
  const listeners = new Set<() => void>();
  return Object.freeze({
    get: (): LandingPreferences => current,
    set(name: keyof LandingPreferences, value: boolean) {
      if (current[name] === value) return;
      current = Object.freeze({ ...current, [name]: value });
      try { storage?.setItem(keys[name], value ? "on" : "off"); } catch { /* The choice holds for this run of the window. */ }
      for (const listener of [...listeners]) listener();
    },
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  });
}

export type LandingPreferenceStore = ReturnType<typeof createLandingPreferences>;

function windowStorage(): Store | null {
  try { return typeof localStorage === "undefined" ? null : localStorage; } catch { return null; }
}

/** The preferences of the landing page of this window. */
export const landingPreferences = createLandingPreferences(windowStorage());

/** The preferences of the landing page, drawn again when one changes. */
export function useLandingPreferences(store: LandingPreferenceStore = landingPreferences): LandingPreferences {
  return useSyncExternalStore(store.subscribe, store.get, store.get);
}
