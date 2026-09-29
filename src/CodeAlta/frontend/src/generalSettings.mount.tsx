import { HTMLSelect } from "@blueprintjs/core";
import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkspaceSnapshot } from "#neoastra";
import { GeneralSettings } from "./GeneralSettings";
import { ProjectRailToggle } from "./ProjectRailToggle";
import { projectRailProjection } from "./projectRail";
import { projectRailVisible } from "./projectRailVisibility";
import { useWindowPreferences } from "./windowPreferences";

const snapshot: WorkspaceSnapshot = {
  configured: true, projects: [{ id: "a", name: "Alpha", path: "/a", archived: false },
    { id: "z", name: "Zeta", path: "/z", archived: false }],
  sessions: [{ messageCount: null, createdAt: null, id: "s", title: "S", fullTitle: "S", fullTitleTruncated: false, parentSessionId: null,
    scopeKind: "project", projectId: "z", lineageIssue: null, workspacePath: "/z", providerKey: null,
    updatedAt: "2026-01-01T00:00:00Z" }],
  projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
};

function Window() {
  const { theme, setTheme, projectSort, setProjectSort, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices, recentSessionCount, setRecentSessionCount } = useWindowPreferences();
  const [settings, setSettings] = useState(true);
  const [narrow, setNarrow] = useState(window.innerWidth <= 875);
  useEffect(() => { document.documentElement.dataset.theme = theme; }, [theme]);
  useEffect(() => {
    const change = () => { setNarrow(window.innerWidth <= 875); closeNarrowRail(); };
    window.addEventListener("resize", change);
    return () => window.removeEventListener("resize", change);
  }, []);
  const visible = projectRailVisible(railState, narrow);
  return <><header><button type="button" onClick={() => setSettings(!settings)}>{settings ? "Workspace" : "Settings"}</button>
    <ProjectRailToggle expanded={visible} onToggle={() => toggleRail(narrow)} buttonRef={null} /></header>
    {settings ? <main className="configuration-page"><div className="settings-grid"><GeneralSettings theme={theme} setTheme={setTheme}
      sort={projectSort} setSort={setProjectSort} desktopCollapsed={railState.desktopCollapsed}
      setDesktopCollapsed={setDesktopCollapsed} notices={notices} recentSessionCount={recentSessionCount} setRecentSessionCount={setRecentSessionCount} /></div></main>
      : <main><aside id="project-rail" hidden={!visible} aria-label="Projects"><HTMLSelect id="project-sort" aria-label="Sort projects" value={projectSort}
          onChange={event => setProjectSort(event.target.value as "name" | "recent")}>
          <option value="name">Name</option><option value="recent">Recent visible updates</option></HTMLSelect>
          <ul id="project-list">{projectRailProjection(snapshot, "", projectSort).projects.map(project => <li key={project.id}>{project.name}</li>)}</ul></aside>
        <p id="current-selection">Selected project and draft remain unchanged</p></main>}
    </>;
}

const host = document.getElementById("app")!;
let root = createRoot(host);
function mount() { root.render(<Window />); }
Object.assign(window, { remountPreferences() { root.unmount(); root = createRoot(host); mount(); } });
mount();
