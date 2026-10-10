// The end of the title bar as the window lays it out: a populated tab strip, Documentation, the zoom and the
// theme switch, before the native caption buttons. The host is a fake one that steps as the real one does.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { Button } from "@blueprintjs/core";
import { Model } from "flexlayout-react";
import type { DesktopShellPreferences, WorkspaceSnapshot } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { ShellLanguageContext } from "./shellLanguage";
import { WindowZoom, zoomWindow, type WindowZoomCommand } from "./WindowZoom";
import { SessionTabStrip } from "./SessionTabStrip";
import { createSessionTabModel } from "./sessionTabLayout";
import { emptySessionTabs } from "./sessionTabs";
import { fileNodeId, type FileTab, type FileTabs } from "./fileTabs";

// DesktopPreferences.ZoomSteps.
const steps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];
const ran: WindowZoomCommand[] = [];
const host = { zoom: 100 };
const preferences = (): DesktopShellPreferences => ({ status: "ok", onClose: "ask", canKeepRunning: true, platform: "windows", entryAdded: false, reviewPermissions: false, inheritPermissions: false,
  sessionWidth: 100, sessionWidths: null, trayIcon: true, zoom: host.zoom });
const zoom = async (request: { direction: number }) => {
  host.zoom = request.direction > 0 ? steps.find(step => step > host.zoom) ?? host.zoom : request.direction < 0 ? [...steps].reverse().find(step => step < host.zoom) ?? host.zoom : 100;
  return preferences();
};
const projects = Array.from({ length: 5 }, (_, index) => ({ id: `project-${index}`, name: `A restored project with a long descriptive name ${index}`, path: `/fixture/project-${index}`, archived: false }));
const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false, projects, sessions: [] };
const open: FileTab[] = [...projects.map(project => ({ projectId: project.id, projectPath: project.path, view: "editor" as const })),
  { projectId: "", projectPath: "", view: "canvas", pluginKey: "builtin:statistics", canvasId: "statistics", key: "session:example",
    name: "Statistics: A long-named session among the restored tabs", icon: "chart-column" }];
const files: FileTabs = { open, active: open[0], closed: [] };
const sessions = emptySessionTabs();

function Fixture() {
  const [layout] = useState(() => {
    const json = createSessionTabModel().toJson();
    json.layout.children = [open.slice(0, 2), open.slice(2)].map((tabs, index) => ({ type: "tabset", id: `pane-${index}`, weight: 50,
      active: index === 0, selected: 0, children: tabs.map(tab => ({ type: "tab", id: fileNodeId(tab), name: tab.view, component: "editor" })) }));
    return Model.fromJson(json);
  });
  // Null where there is no shell: the page in a browser.
  const [shell, setShell] = useState<DesktopShellPreferences | null>(preferences);
  const run = (command: WindowZoomCommand) => { ran.push(command); void zoomWindow(command, zoom, setShell); };
  Object.assign(window, { windowZoom: { ran, run, setShell: (shown: boolean) => setShell(shown ? preferences() : null),
    setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } } });
  return <div className="ide-shell" style={{ position: "relative", height: 300 }}>
    <SessionTabStrip layout={layout} state={sessions} snapshot={snapshot} files={files} capture={() => () => true}
      select={() => { }} close={() => { }} reopen={() => { }} renderFile={tab => <div>{tab.name ?? tab.projectId}</div>}>New session</SessionTabStrip>
    <div className="window-actions">
      <Button variant="minimal" size="small" className="documentation-open" icon={<AppIcon name="documentation" size={16} />} aria-label="Documentation" />
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
