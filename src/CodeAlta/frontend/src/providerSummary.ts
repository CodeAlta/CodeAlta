import type { ModelCatalogProvidersResponse } from "#neoastra";

export function providerSummary(page: ModelCatalogProvidersResponse | undefined, epoch: string) {
  const providers = page?.epoch === epoch && page.status === "ok" ? page.providers.filter(provider => provider.enabled) : undefined;
  return {
    ready: providers?.filter(provider => provider.availability === "Ready").length ?? 0,
    errors: providers?.filter(provider => ["Failed", "Unsupported"].includes(provider.availability)).length ?? 0,
    detecting: !providers || providers.some(provider => ["Unknown", "Probing"].includes(provider.availability)),
  };
}
