import { useEffect, useState } from "react";
import { modelCatalog, type ModelCatalogModel, type ModelCatalogProvider } from "#neoastra";

// As many models of a provider as the host sends (ModelCatalogService.MaximumModels): Azure OpenAI lists several hundred.
export const maximumProviderModels = 2048;

type Catalog = Pick<typeof modelCatalog, "providers" | "models">;

/** The configured providers of a host and the models each ready one lists, read once for the host. */
export function useProviderModels(epoch: string | null | undefined, catalog: Catalog = modelCatalog)
  : Readonly<{ providers: readonly ModelCatalogProvider[]; models: ReadonlyMap<string, readonly ModelCatalogModel[]> }> {
  const [providers, setProviders] = useState<readonly ModelCatalogProvider[]>([]);
  const [models, setModels] = useState<ReadonlyMap<string, readonly ModelCatalogModel[]>>(new Map());
  useEffect(() => {
    setProviders([]); setModels(new Map());
    if (!epoch) return;
    const controller = new AbortController();
    const options = { signal: controller.signal, timeoutMilliseconds: 15_000 };
    void catalog.providers({ expectedEpoch: epoch }, options).then(reply => {
      if (controller.signal.aborted || reply.status !== "ok" || reply.epoch !== epoch || !Array.isArray(reply.providers)) return;
      const listed = reply.providers.slice(0, 32);
      setProviders(listed);
      for (const provider of listed.filter(provider => provider.enabled)) {
        void catalog.models({ expectedEpoch: epoch, providerId: provider.id }, options).then(page => {
          if (controller.signal.aborted || page.status !== "ok" || page.epoch !== epoch || !Array.isArray(page.models)) return;
          setModels(current => new Map(current).set(provider.id, page.models.slice(0, maximumProviderModels)));
        }).catch(() => { /* A provider whose models cannot be read offers none. */ });
      }
    }).catch(() => { /* No provider is offered. */ });
    return () => controller.abort();
  }, [epoch, catalog]);
  return { providers, models };
}
