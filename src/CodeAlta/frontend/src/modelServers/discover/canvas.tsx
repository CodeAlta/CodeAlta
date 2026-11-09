import { useContext, useMemo } from "react";
import { LandingShellContext } from "../../landing/landingShell";
import { useVisible } from "../../pluginScript/codealta";
import { settingsNavigation } from "../../settingsNavigation";
import { showToast } from "../../appToaster";
import { modelServersApi } from "../api";
import { DiscoverModels, type DiscoverActions } from "./DiscoverModels";

// The module of the Discover models canvas, the one the built-in Fleet plugin names with `PluginScript.App("models")`. It asks the
// `modelServers` service of the window, for the host of the window the shell around the tabs of canvases lends it. Its styles are
// those of the page (main.tsx imports discover.css), as the other canvases of the application.

const actions: DiscoverActions = {
  notify: (message, intent) => showToast({ message, intent, icon: intent === "success" ? "tick" : "error", timeout: intent === "success" ? 8000 : 12_000 }),
  openServers: () => settingsNavigation.open("modelServers"),
};

/** The Discover models canvas, for the window to draw in a tab. */
export default function Canvas() {
  const visible = useVisible();
  const shell = useContext(LandingShellContext);
  const epoch = shell?.epoch ?? null;
  const api = useMemo(() => epoch ? modelServersApi(epoch) : null, [epoch]);
  return <DiscoverModels api={api} visible={visible} actions={actions} />;
}
