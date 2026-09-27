import { useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkspaceSnapshot } from "#neoastra";
import { ProjectRailRows } from "./ProjectRailRows";
import { ProjectDetailsEntry, type ProjectDetailsContext } from "./ProjectDetailsEntry";
import { InventoryLanguageFixture } from "./inventoryLanguage.mount";

const projects = [
  { id: "one", name: "Alpha", path: "C:/catalog/alpha", archived: false },
  { id: "two", name: "Beta", path: "C:/catalog/beta", archived: false },
  { id: "old", name: "Legacy", path: "C:/catalog/legacy", archived: true },
];
const original: WorkspaceSnapshot = { configured: true, projects, sessions: [],
  projectsTruncated: true, sessionsTruncated: true, displayTextTruncated: true };
const initial: ProjectDetailsContext = { snapshot: original, projectId: "one", sessionId: "session-one",
  hostEpoch: "test-host", hostAvailable: true, refreshVersion: 1, refreshReady: true, active: true };
const copied: string[] = [];
const pending: Array<{ resolve: () => void; reject: () => void }> = [];
let copyMode: "success" | "pending" | "reject" | "unavailable" = "success";
Object.defineProperty(navigator, "clipboard", { configurable: true, get: () => copyMode === "unavailable" ? undefined : {
  writeText: (text: string) => { copied.push(text);
    if (copyMode === "reject") return Promise.reject(new Error("private clipboard failure"));
    if (copyMode === "pending") return new Promise<void>((resolve, reject) => pending.push({ resolve, reject }));
    return Promise.resolve();
  },
} });
const fixture = { copied, get pending() { return pending.length; },
  copyMode: (value: typeof copyMode) => { copyMode = value; },
  resolve: () => pending.shift()?.resolve(), reject: () => pending.shift()?.reject(),
  select: (_id: string | null) => {}, session: (_id: string | null) => {}, host: (_epoch: string | null, _available: boolean) => {},
  active: (_value: boolean) => {}, snapshot: (_value: WorkspaceSnapshot | undefined) => {},
  stale: (_value: WorkspaceSnapshot | undefined) => {}, refresh: (_ok: boolean) => {},
  get current() { return initial as ProjectDetailsContext; },
};
Object.assign(window, { projectDetailsFixture: fixture });

function App() {
  const [context, setContext] = useState<ProjectDetailsContext>(initial);
  const current = useRef(context);
  Object.defineProperty(fixture, "current", { configurable: true, get: () => current.current });
  function change(next: ProjectDetailsContext) { current.current = next; setContext(next); }
  fixture.select = id => change({ ...current.current, projectId: id, sessionId: id === null ? "global-session" : `session-${id}` });
  fixture.session = id => change({ ...current.current, sessionId: id });
  fixture.host = (epoch, available) => change({ ...current.current, hostEpoch: epoch, hostAvailable: available });
  fixture.active = active => change({ ...current.current, active });
  fixture.snapshot = snapshot => change({ ...current.current, snapshot, refreshVersion: current.current.refreshVersion + 1, refreshReady: !!snapshot });
  fixture.stale = snapshot => { current.current = { ...current.current, snapshot }; };
  fixture.refresh = ok => { const next = { ...current.current, refreshVersion: current.current.refreshVersion + 1, refreshReady: ok };
    change(ok ? { ...next, snapshot: { ...original } } : next); };
  return <div className="app-shell"><div className="workspace-shell project-rail-open" style={{ "--project-pane-width": "180px", "--session-pane-width": "290px" } as React.CSSProperties}>
    <aside className="project-rail"><ProjectRailRows projects={[...(context.snapshot?.projects ?? [])]} selectedId={context.projectId}
      onSelect={fixture.select} canRename={false} renameBusy={false} onRename={() => { throw new Error("read-only fixture"); }} /></aside>
    <aside className="session-rail"><div className="session-rail-header"><div><span className="eyebrow">Sessions</span>
      <h2>{context.snapshot?.projects.find(project => project.id === context.projectId)?.name ?? "Other sessions"}</h2></div>
      <div className="session-rail-actions"><ProjectDetailsEntry context={context} getCurrent={() => current.current} /></div></div></aside>
    <main className="content"><p id="selection">{context.projectId ?? "global"} / {context.sessionId ?? "none"}</p></main>
  </div></div>;
}
createRoot(document.getElementById("app")!).render(<InventoryLanguageFixture><App /></InventoryLanguageFixture>);
