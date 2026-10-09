// The end of the title bar as the window lays it out: the last tab strip, the zoom of the window and the theme
// switch, before the room of the native caption buttons. The host is a fake one that steps as the real one does.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { Button } from "@blueprintjs/core";
import type { DesktopShellPreferences } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { ShellLanguageContext } from "./shellLanguage";
import { WindowZoom, zoomWindow, type WindowZoomCommand } from "./WindowZoom";

// DesktopPreferences.ZoomSteps.
const steps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];
const ran: WindowZoomCommand[] = [];
const host = { zoom: 100 };
const preferences = (): DesktopShellPreferences => ({ status: "ok", onClose: "ask", canKeepRunning: true, platform: "windows", entryAdded: false, reviewPermissions: false,
  sessionWidth: 100, sessionWidths: null, trayIcon: true, zoom: host.zoom });
const zoom = async (request: { direction: number }) => {
  host.zoom = request.direction > 0 ? steps.find(step => step > host.zoom) ?? host.zoom : request.direction < 0 ? [...steps].reverse().find(step => step < host.zoom) ?? host.zoom : 100;
  return preferences();
};

function Fixture() {
  // Null where there is no shell: the page in a browser.
  const [shell, setShell] = useState<DesktopShellPreferences | null>(preferences);
  const run = (command: WindowZoomCommand) => { ran.push(command); void zoomWindow(command, zoom, setShell); };
  Object.assign(window, { windowZoom: { ran, run, setShell: (shown: boolean) => setShell(shown ? preferences() : null),
    setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } } });
  return <div className="ide-shell" style={{ position: "relative", height: 300 }}>
    <div className="flexlayout__tabset_tabbar_outer_top" data-titlebar-end data-neoastra-drag-region><div className="tabs" style={{ flex: "1 1 auto" }} /></div>
    <div className="window-actions">
      {shell && <WindowZoom zoom={shell.zoom} run={run} />}
      <Button variant="minimal" size="small" className="theme-switch" icon={<AppIcon name="themeDark" size={16} />} aria-label="Theme" />
    </div>
  </div>;
}
// The room the native caption buttons take at the end of the title bar, as the host publishes it.
document.documentElement.style.setProperty("--neoastra-titlebar-inset-right", "138px");
document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
