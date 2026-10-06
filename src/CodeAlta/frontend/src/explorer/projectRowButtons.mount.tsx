// The rows of the Explorer alone, in a rail as wide as the one the window starts with: where the buttons of a row are.
import { createRoot } from "react-dom/client";
import type { WorkspaceProject } from "#neoastra";
import { ProjectRailRows } from "./ProjectRailRows";

const project = (id: string, name: string, archived = false): WorkspaceProject => ({ id, name, path: `/${id}`, archived });
// A project is named after what it has open; the name of "none" is longer than its row.
const projects = [project("editor", "Editor"), project("changes", "Changes"), project("terminal", "Terminal"), project("both", "Both"),
  project("none", "A project whose name is longer than its row can show"), project("old", "Old", true)];
const nothing = () => { };

createRoot(document.getElementById("root")!).render(<div className="ide-shell" style={{ width: 272 }}>
  <aside className="project-rail">
    <ProjectRailRows projects={projects} selectedId={null} onSelect={nothing} canRename renameBusy={false} onRename={nothing}
      // No menu is opened here: the button of a menu is only laid out.
      actions={{ current: () => { throw new Error("no menu is opened"); }, open: nothing, rename: nothing, archive: nothing }}
      tree={{ expanded: () => false, toggle: nothing, favorite: id => id === "both", setFavorite: nothing, sessions: () => null }}
      editor={{ open: id => id === "editor" || id === "both", unsaved: () => false, show: nothing }}
      changes={{ open: id => id === "changes" || id === "both", show: nothing }}
      terminals={{ count: id => id === "terminal" ? 2 : id === "both" ? 1 : 0, create: nothing }} />
  </aside>
</div>);
