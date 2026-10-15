import type { ModelCatalogProvidersResponse } from "#neoastra";

/** Counts the enabled providers that are ready, that fail, and whether some are still detected; `only` keeps the providers of a session's space. */
export function providerSummary(page: ModelCatalogProvidersResponse | undefined, epoch: string, only: readonly string[] | null = null) {
  const providers = page?.epoch === epoch && page.status === "ok"
    ? page.providers.filter(provider => provider.enabled && (!only || only.includes(provider.id))) : undefined;
  return {
    ready: providers?.filter(provider => provider.availability === "Ready").length ?? 0,
    errors: providers?.filter(provider => ["Failed", "Unsupported"].includes(provider.availability)).length ?? 0,
    detecting: !providers || providers.some(provider => ["Unknown", "Probing"].includes(provider.availability)),
  };
}
