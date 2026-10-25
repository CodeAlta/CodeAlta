/**
 * What the list of the providers in Settings remembers between starts: the favorite providers, listed first, and
 * whether the enabled providers come before the disabled ones. Like the favorite projects, it is a preference of
 * the window: it changes nothing of the configuration.
 */
export type ProviderListPreferences = Readonly<{
  /** The enabled providers are listed before the disabled ones. */
  enabledFirst: boolean;
  /** The favorite providers, by key in lower case, in the order they were added. */
  favorites: readonly string[];
}>;

export const providerListKey = "codealta.desktop.providerList.v1";
export const defaultProviderListPreferences: ProviderListPreferences = Object.freeze({ enabledFirst: true, favorites: [] });
// Most favorites kept, and the longest key kept.
const limit = 256;
const longestKey = 256;

/** The stored preferences, or the defaults when nothing is stored or it cannot be read. */
export function restoreProviderListPreferences(read: () => string | null): ProviderListPreferences {
  try {
    const raw = read();
    if (raw === null) return defaultProviderListPreferences;
    const value = JSON.parse(raw) as unknown;
    if (!value || typeof value !== "object" || Array.isArray(value)) return defaultProviderListPreferences;
    const stored = value as Record<string, unknown>;
    const favorites = new Set<string>();
    if (Array.isArray(stored.favorites))
      for (const item of stored.favorites)
        if (typeof item === "string" && item && item.length <= longestKey && favorites.size < limit) favorites.add(item.toLowerCase());
    return { enabledFirst: typeof stored.enabledFirst === "boolean" ? stored.enabledFirst : defaultProviderListPreferences.enabledFirst, favorites: [...favorites] };
  } catch { return defaultProviderListPreferences; }
}

export function persistProviderListPreferences(write: (value: string) => void, preferences: ProviderListPreferences): boolean {
  try { write(JSON.stringify({ enabledFirst: preferences.enabledFirst, favorites: preferences.favorites })); return true; }
  catch { return false; }
}

export const isFavoriteProvider = (preferences: ProviderListPreferences, key: string): boolean => preferences.favorites.includes(key.toLowerCase());

/** Makes a provider a favorite, or no longer one; the same preferences are returned when nothing changes. */
export function setFavoriteProvider(preferences: ProviderListPreferences, key: string, favorite: boolean): ProviderListPreferences {
  const id = key.toLowerCase();
  if (!id || id.length > longestKey || isFavoriteProvider(preferences, id) === favorite) return preferences;
  return { ...preferences, favorites: favorite ? [...preferences.favorites, id].slice(-limit) : preferences.favorites.filter(value => value !== id) };
}

/** A favorite provider given another key stays a favorite under it; the same preferences are returned when it was not one. */
export function renameFavoriteProvider(preferences: ProviderListPreferences, from: string, to: string): ProviderListPreferences {
  const [previous, next] = [from.toLowerCase(), to.toLowerCase()];
  if (previous === next || !isFavoriteProvider(preferences, previous)) return preferences;
  return setFavoriteProvider({ ...preferences, favorites: preferences.favorites.filter(value => value !== previous) }, next, true);
}

/** A group of the list: its favorite providers, its enabled or disabled ones, or the others after the favorites. */
export type ProviderGroupKind = "favorites" | "enabled" | "disabled" | "others";
export type ProviderGroup<T> = Readonly<{ kind: ProviderGroupKind; providers: readonly T[] }>;

/**
 * The providers as the list shows them, in groups that are never empty: the favorites first, then, with
 * `enabledFirst`, the enabled providers and the disabled ones, else all the others. Each group keeps the order of
 * the configuration; among the favorites, the enabled ones come first with `enabledFirst`.
 */
export function groupProviders<T extends Readonly<{ key: string; enabled: boolean }>>(providers: readonly T[], preferences: ProviderListPreferences): ProviderGroup<T>[] {
  const favorites = providers.filter(provider => isFavoriteProvider(preferences, provider.key));
  const others = providers.filter(provider => !isFavoriteProvider(preferences, provider.key));
  const groups: ProviderGroup<T>[] = preferences.enabledFirst
    ? [{ kind: "favorites", providers: [...favorites.filter(provider => provider.enabled), ...favorites.filter(provider => !provider.enabled)] },
      { kind: "enabled", providers: others.filter(provider => provider.enabled) }, { kind: "disabled", providers: others.filter(provider => !provider.enabled) }]
    : [{ kind: "favorites", providers: favorites }, { kind: "others", providers: others }];
  return groups.filter(group => group.providers.length > 0);
}

/** The providers in the order the list shows them. */
export const orderProviders = <T extends Readonly<{ key: string; enabled: boolean }>>(providers: readonly T[], preferences: ProviderListPreferences): T[] =>
  groupProviders(providers, preferences).flatMap(group => group.providers);
