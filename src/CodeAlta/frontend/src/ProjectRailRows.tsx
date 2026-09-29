import type { WorkspaceProject } from "#neoastra";
import { Fragment, useState, type ReactNode } from "react";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import { ProjectRowActions, type ProjectRowAuthority } from "./ProjectRowActions";

export function ProjectRailRows({ projects, selectedId, onSelect, actions, children, activity }: {
  activity?: (projectId: string | null) => ReactNode;
  projects: WorkspaceProject[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  canRename: boolean;
  renameBusy: boolean;
  onRename: () => void;
  actions?: ProjectRowAuthority;
  children?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const [folded, setFolded] = useState<{ id: string | null } | null>(null);
  const expanded = folded?.id !== selectedId;
  function select(id: string | null) {
    if (id === selectedId) setFolded(expanded ? { id } : null);
    else setFolded(null);
    onSelect(id);
  }
  return <>
    <ul id="project-list" className="nav-list project-list" aria-label={t("Projects")}>
      {projects.map(project => <Fragment key={project.id}><ProjectRowActions project={project} authority={actions}><button type="button" title={`${project.name}\n${project.path}`} aria-pressed={selectedId === project.id}
        aria-expanded={selectedId === project.id && expanded} onClick={() => select(project.id)}>
        <AppIcon name="chevronDown" size={12} className={selectedId === project.id && expanded ? "tree-chevron expanded" : "tree-chevron"} />
        <AppIcon name="folder" size={15} /><strong>{project.name}</strong>{activity?.(project.id)}{project.archived && <small>{t("Archived")}</small>}
      </button></ProjectRowActions>{selectedId === project.id && <li className="project-session-branch" hidden={!expanded}>{children}</li>}</Fragment>)}
    </ul>
    <ul className="nav-list project-root-list" aria-label={t("Other sessions")}>
      <li><button type="button" aria-pressed={selectedId === null} aria-expanded={selectedId === null && expanded} onClick={() => select(null)}>
        <AppIcon name="chevronDown" size={12} className={selectedId === null && expanded ? "tree-chevron expanded" : "tree-chevron"} /><AppIcon name="folder" size={15} /><strong>{t("Other sessions")}</strong>{activity?.(null)}
      </button></li>
      {selectedId === null && <li className="project-session-branch" hidden={!expanded}>{children}</li>}
    </ul>
  </>;
}
