import { useEffect, useState } from "react";
import { Button } from "@blueprintjs/core";
import { modelCatalog, type ModelCatalogProvidersResponse } from "#neoastra";
import { useShellLanguage } from "./shellLanguage";

export function ActiveProviderStatus({ epoch, onOpen }: { epoch: string; onOpen: () => void }) {
  const { t } = useShellLanguage();
  const [page, setPage] = useState<ModelCatalogProvidersResponse>();
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function read() {
      try {
        const value = await modelCatalog.providers({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 8000 });
        if (!controller.signal.aborted) setPage(value.epoch === epoch && value.status === "ok" ? value : undefined);
      } catch { if (!controller.signal.aborted) setPage(undefined); }
      if (!controller.signal.aborted) timer = setTimeout(read, 10000);
    }
    void read();
    return () => { clearTimeout(timer); controller.abort(); };
  }, [epoch]);
  const providers = page?.epoch === epoch ? page.providers.filter(provider => provider.enabled) : undefined;
  return <Button variant="minimal" className="active-provider-status" onClick={onOpen} aria-label={t("Active providers")}
    title={providers?.map(provider => `${provider.name}: ${provider.availability}`).join("\n") ?? t("Provider inventory unavailable.")}>
    {providers?.map(provider => <span key={provider.id} className="provider-status-item" data-state={provider.availability.toLowerCase()}>
      <span aria-hidden="true">●</span> {provider.name}
    </span>) ?? t("Providers")}
  </Button>;
}
