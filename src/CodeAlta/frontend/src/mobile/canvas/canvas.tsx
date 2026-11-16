import { useMemo } from "react";
import { useAlta } from "../../pluginScript/PluginScript";
import { useVisible } from "../../pluginScript/codealta";
import { useShellLanguage } from "../../shellLanguage";
import { createMobileApi } from "./api";
import { MobileCanvas } from "./MobileCanvas";
import { useMobileCanvas } from "./useMobileCanvas";

// The module of the Mobile tab, the one the built-in Mobile plugin names with `PluginScript.App("mobile")`: it follows the status of the plugin over
// `alta.rpc` while the tab is shown, and draws it.

/** The Mobile tab, for the window to draw in a tab. */
export default function Canvas() {
  const alta = useAlta();
  const visible = useVisible();
  const { t } = useShellLanguage();
  const api = useMemo(() => createMobileApi(alta.rpc), [alta]);
  const host = useMemo(() => ({
    openLink: (url: string) => alta.host.openLink(url),
    copyFailed: () => alta.host.notify(t("The address could not be copied."), { tone: "warning" }),
  }), [alta, t]);
  const { status, failure, actions } = useMobileCanvas(api, host, visible);
  return <MobileCanvas status={status} failure={failure} actions={actions} />;
}
