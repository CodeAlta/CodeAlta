import { useEffect, useMemo, useRef, useState } from "react";
import { useAlta } from "../../pluginScript/PluginScript";
import { useVisible } from "../../pluginScript/codealta";
import { useShellLanguage } from "../../shellLanguage";
import { createMobileApi } from "./api";
import { MobileCanvas, type MobileCanvasActions } from "./MobileCanvas";
import type { MobileStatus } from "./status";

// The module of the Mobile tab, the one the built-in Mobile plugin names with `PluginScript.App("mobile")`: it follows the status of the plugin over
// `alta.rpc` while the tab is shown, and draws it.

/** The Mobile tab, for the window to draw in a tab. */
export default function Canvas() {
  const alta = useAlta();
  const visible = useVisible();
  const { t } = useShellLanguage();
  const api = useMemo(() => createMobileApi(alta.rpc), [alta]);
  const [status, setStatus] = useState<MobileStatus | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [watching, setWatching] = useState(0);
  // A hidden tab follows nothing; shown again, it listens again and reads the status.
  useEffect(() => {
    if (!visible) return;
    return api.watch(event => {
      if (event.kind === "status") { setStatus(event.status); setFailure(null); } else setFailure(event.message);
    });
  }, [api, visible, watching]);
  // What the actions reach is read when they run.
  const latest = useRef({ alta, t });
  latest.current = { alta, t };
  const actions = useMemo<MobileCanvasActions>(() => ({
    retry: async () => {
      try { setStatus(await api.retry()); setFailure(null); }
      catch (error) { setFailure(error instanceof Error ? error.message : String(error)); }
    },
    copy: async text => {
      try { await navigator.clipboard.writeText(text); return true; }
      catch { latest.current.alta.host.notify(latest.current.t("The address could not be copied."), { tone: "warning" }); return false; }
    },
    openLink: url => latest.current.alta.host.openLink(url),
    reload: () => setWatching(value => value + 1),
  }), [api]);
  return <MobileCanvas status={status} failure={failure} actions={actions} />;
}
