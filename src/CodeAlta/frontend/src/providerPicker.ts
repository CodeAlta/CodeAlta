import { isFavoriteProvider, type ProviderListPreferences } from "./providerListOrder";

// The provider list of the composer: what a search keeps of it, and in which order and groups it lists the providers.

/** A provider as the list names it: its key, the name Settings gives it, and the type of its adapter when known. */
export type ProviderPickerChoice = Readonly<{ id: string; name: string; type?: string | null }>;

/** The providers the list shows: the favorites of Settings first, then the others, each group by name. */
export type ProviderPickerSections<T> = Readonly<{ favorites: readonly T[]; others: readonly T[] }>;

const names = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });

const shownName = (provider: ProviderPickerChoice) => provider.name.trim() || provider.id;

/** The providers whose name, key or type holds every word of the search; all of them for an empty search. */
export function matchingProviders<T extends ProviderPickerChoice>(providers: readonly T[], query: string): readonly T[] {
  const terms = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return providers;
  return providers.filter(provider => {
    const text = `${provider.name} ${provider.id} ${provider.type ?? ""}`.toLocaleLowerCase();
    return terms.every(term => text.includes(term));
  });
}

/** The providers that match the search, the favorites first, each group sorted by name, then by key. */
export function sectionProviders<T extends ProviderPickerChoice>(providers: readonly T[], query: string, preferences: ProviderListPreferences): ProviderPickerSections<T> {
  const sorted = [...matchingProviders(providers, query)].sort((left, right) => names.compare(shownName(left), shownName(right)) || names.compare(left.id, right.id));
  return { favorites: sorted.filter(provider => isFavoriteProvider(preferences, provider.id)), others: sorted.filter(provider => !isFavoriteProvider(preferences, provider.id)) };
}

/**
 * What the list says beside the name of a provider: its key when its name is another, and the type of its adapter
 * when it says more than the name and the key; null when there is nothing more to say.
 */
export function providerPickerDetail(provider: ProviderPickerChoice): string | null {
  const name = shownName(provider).toLocaleLowerCase(), key = provider.id.toLocaleLowerCase(), type = provider.type?.trim() ?? "";
  const parts = [key !== name ? provider.id : null, type && type.toLocaleLowerCase() !== key && type.toLocaleLowerCase() !== name ? type : null];
  return parts.filter(Boolean).join(" · ") || null;
}
