import { useEffect, useState } from "react";
import { useShellLanguage } from "./shellLanguage";

// Do not flash raw transport diagnostics for an isolated missed background read.
export function ObservationStatus({ unavailable }: { unavailable: boolean }) {
  const { t } = useShellLanguage();
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    if (!unavailable) { setVisible(false); return; }
    const timer = setTimeout(() => setVisible(true), 3000);
    return () => clearTimeout(timer);
  }, [unavailable]);
  return unavailable && visible ? <span className="observation-status" role="status">{t("Reconnecting… Your draft is safe.")}</span> : null;
}
