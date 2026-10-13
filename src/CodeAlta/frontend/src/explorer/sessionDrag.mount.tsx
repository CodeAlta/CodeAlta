// A session row and the rows of two projects, with the pointer drag of the Explorer: what a drag drops, and the
// clicks that reach the session row.
import { createRoot } from "react-dom/client";
import type { WorkspaceProject } from "#neoastra";
import { useSessionRowDrag, type SessionDrag } from "./sessionDrag";

type Recorded = { clicks: string[]; drops: string[] };
const recorded: Recorded = { clicks: [], drops: [] };
(window as unknown as { recorded: Recorded }).recorded = recorded;

const projects: WorkspaceProject[] = [{ id: "stride", name: "stride", path: "/stride", archived: false },
  { id: "stride2", name: "stride2", path: "/stride2", archived: false }];
const drag: SessionDrag = { id: "s1", title: "Session one", from: { projectId: "stride", projectPath: "/stride" } };

function Fixture() {
  const rows = useSessionRowDrag(id => projects.find(project => project.id === id),
    (dropped, project) => recorded.drops.push(`${dropped.id}->${project.id}`));
  return <div style={{ width: 300 }}>
    <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
      {projects.map(project => <li key={project.id} className="project-action-row" style={{ height: 40 }}>
        <button type="button" data-scope={project.id} data-over={rows.view?.over === project.id ? "true" : undefined}
          style={{ width: "100%", height: 40 }}>{project.name}</button>
      </li>)}
    </ul>
    <div id="session" className="session-row" style={{ height: 40, marginTop: 40 }} {...rows.source(drag)}
      onClick={() => recorded.clicks.push("s1")}>Session one</div>
    {rows.view && <div id="label" style={{ position: "fixed", left: rows.view.x, top: rows.view.y, pointerEvents: "none" }}>{rows.view.title}</div>}
  </div>;
}

createRoot(document.getElementById("root")!).render(<Fixture />);
