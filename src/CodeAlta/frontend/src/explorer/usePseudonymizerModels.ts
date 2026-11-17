import { useEffect, useRef, useState } from "react";
import type { pseudonymizer, PseudonymizerModelsResponse } from "#neoastra";
import { downloading } from "./projectSecurity";

/**
 * The languages and their models as the host has them, read again every second while a model is downloaded. The setter
 * takes the answer of a call that changed them; `null` while they are not read or cannot be.
 */
export function usePseudonymizerModels(api: Pick<typeof pseudonymizer, "models">, epoch: string) {
  const [models, setModels] = useState<PseudonymizerModelsResponse | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const running = downloading(models);
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = () => void api.models({ expectedEpoch: epoch }, { timeoutMilliseconds: 10_000 }).then(answer => {
      if (!alive.current) return;
      setModels(answer.status === "ok" ? answer : null);
      if (downloading(answer)) timer = setTimeout(read, 1000);
    }, () => { if (alive.current) setModels(null); });
    read();
    return () => clearTimeout(timer);
  }, [api, epoch, running]);
  return [models, setModels] as const;
}
