import { useContext, useEffect, useState } from "react";
import { Button } from "@blueprintjs/core";
import { modelCatalog, type ModelCatalogProvidersResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { HostReadsContext, PaneObservingContext } from "./hostReadsContext";
import { useShellLanguage } from "./shellLanguage";
import { providerSummary } from "./providerSummary";

/** What the providers are doing, as the composer reports it: ready ones, failing ones, and whether they are being detected. */
export type ActiveProviderState = Readonly<{ ready: number; errors: number; detecting: boolean }>;

/**
 * The providers of the window, in the `⋯` menu of the composer: how many are ready and how many fail, read every ten
 * seconds while its pane is shown, through the read the panes of the window share; it opens the Providers page.
 * `onState` reports what it read, so that the menu can show a failing one.
 */
export function ActiveProviderStatus({ epoch, onOpen, onState, providers = null }: { epoch: string; onOpen: () => void; onState?: (state: ActiveProviderState) => void;
  /** The keys of the providers of the session's space, which alone are counted; null counts every provider. */
  providers?: readonly string[] | null }) {
  const { t } = useShellLanguage();
  const [page, setPage] = useState<ModelCatalogProvidersResponse>();
  const readProviders = useContext(HostReadsContext)?.providers ?? modelCatalog.providers;
  const observing = useContext(PaneObservingContext);
  useEffect(() => {
    if (!observing) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function read() {
      try {
        const value = await readProviders({ expectedEpoch: epoch }, { signal: controller.signal });
        if (!controller.signal.aborted && value.epoch === epoch && value.status === "ok") setPage(value);
      } catch { /* A missed background read does not erase the last provider observation. */ }
      if (!controller.signal.aborted) timer = setTimeout(read, 10000);
    }
    void read();
    return () => { clearTimeout(timer); controller.abort(); };
  }, [epoch, readProviders, observing]);
  const { ready, errors, detecting } = providerSummary(page, epoch, providers);
  useEffect(() => { onState?.({ ready, errors, detecting }); }, [ready, errors, detecting, onState]);
  const label = detecting ? t("Detecting providers…") : t(ready === 1 ? "{count} active provider" : "{count} active providers", { count: ready });
  return <Button variant="minimal" className="active-provider-status" onClick={onOpen} aria-label={label}
    intent={errors ? "warning" : "none"} icon={<AppIcon name="provider" size={16} />}
    title={t("Providers")}>
    {label}{errors > 0 && <> · {t(errors === 1 ? "{count} provider error" : "{count} provider errors", { count: errors })}</>}
  </Button>;
}
