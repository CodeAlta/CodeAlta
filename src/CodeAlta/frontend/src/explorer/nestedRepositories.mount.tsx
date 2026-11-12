// The Explorer with a repository whose sub-project has a map of its own (#433): platform-map › Repos › billing › Apps ›
// invoices-api. What the titles of the repositories ask is recorded for the test; folding is kept as the window keeps it.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkspaceProject } from "#neoastra";
import type { Label } from "../labels/labels";
import { RepositoryRailRow } from "../subprojects/RepositoryRailRow";
import { ProjectRailRows } from "./ProjectRailRows";
import { projectRailGroups, projectTitle } from "./projectRail";

const calls: string[] = [];
(window as unknown as { calls: string[] }).calls = calls;
const project = (id: string, name = id): WorkspaceProject => ({ id, name, path: `/${id}`, archived: false });
const sub = (id: string, parent: string, parentName: string): WorkspaceProject =>
  ({ ...project(id), path: `/${parent}/${id}`, parentId: parent, subPath: id, repositoryName: parentName });
const projects = [project("platform", "platform-map"), sub("billing", "platform", "platform-map"), sub("checkout", "platform", "platform-map"),
  sub("invoices", "billing", "billing"), sub("ledger", "billing", "billing"), project("notes")];
const label = (id: string, name: string, projectIds: string[], repository: string): Label =>
  ({ id, name, description: null, icon: null, color: null, order: 1, parent: null, favorite: false, space: null, projectIds, repository });
const labels = [label("platform-map.repos", "Repos", ["billing", "checkout"], "platform"), label("platform-map-billing.apps", "Apps", ["invoices"], "billing"),
  label("platform-map-billing.libs", "Libs", ["ledger"], "billing")];
const nothing = () => { };

function Explorer() {
  const [folded, setFolded] = useState<readonly string[]>([]);
  const fold = (id: string, value: boolean) => setFolded(current => value ? [...current, id] : current.filter(other => other !== id));
  return <aside className="project-rail">
    <ProjectRailRows projects={projects} selectedId={null} onSelect={nothing} canRename={false} renameBusy={false} onRename={nothing}
      tree={{ expanded: () => false, toggle: nothing, favorite: () => false, setFavorite: nothing, sessions: () => null }}
      labels={{
        groups: projectRailGroups(projects, 0, labels), folded: id => folded.includes(id),
        row: group => group.repository
          ? <RepositoryRailRow key={group.label.id} group={group} folded={folded.includes(group.label.id)} onFold={value => fold(group.label.id, value)}
            actions={{ subProjects: id => calls.push(`subprojects ${id}`), setUp: id => calls.push(`setup ${id}`) }} />
          : <li key={group.label.id} className="project-label-row" data-depth={group.depth} data-label-title={group.label.id}>{group.label.name}</li>,
        repository: value => projectTitle(value, projects).repository,
      }} />
  </aside>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell" style={{ width: 320 }}><Explorer /></div></StrictMode>);
