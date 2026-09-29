import { useEffect, useState } from "react";
import { Button } from "@blueprintjs/core";
import { modelCatalog, type ModelCatalogProvidersResponse } from "#neoastra";
import { useShellLanguage } from "./shellLanguage";
import { providerSummary } from "./providerSummary";

export function ActiveProviderStatus({ epoch, onOpen }: { epoch: string; onOpen: () => void }) {
  const { t } = useShellLanguage();
  const [page, setPage] = useState<ModelCatalogProvidersResponse>();
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function read() {
      try {
        const value = await modelCatalog.providers({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 8000 });
        if (!controller.signal.aborted && value.epoch === epoch && value.status === "ok") setPage(value);
      } catch { /* A missed background read does not erase the last provider observation. */ }
      if (!controller.signal.aborted) timer = setTimeout(read, 10000);
    }
    void read();
    return () => { clearTimeout(timer); controller.abort(); };
  }, [epoch]);
  const { ready, errors, detecting } = providerSummary(page, epoch);
  const label = detecting ? t("Detecting providers…") : t(ready === 1 ? "{count} active provider" : "{count} active providers", { count: ready });
  return <Button variant="minimal" className="active-provider-status" onClick={onOpen} aria-label={label}
    intent={errors ? "warning" : ready ? "success" : "none"}
    title={t("Providers")}>
    {label}{errors > 0 && <> · {t("{count} provider errors", { count: errors })}</>}
  </Button>;
}
