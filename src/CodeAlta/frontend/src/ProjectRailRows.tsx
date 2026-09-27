import type { WorkspaceProject } from "#neoastra";
import { useShellLanguage } from "./shellLanguage";
import { ProjectRowActions, type ProjectRowAuthority } from "./ProjectRowActions";

export function ProjectRailRows({ projects, selectedId, onSelect, canRename, renameBusy, onRename, actions }: {
  projects: WorkspaceProject[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  canRename: boolean;
  renameBusy: boolean;
  onRename: () => void;
  actions?: ProjectRowAuthority;
}) {
  const { t } = useShellLanguage();
  return <>
    <ul id="project-list" className="nav-list project-list" aria-label={t("Projects")}>
      {projects.map(project => <ProjectRowActions key={project.id} project={project} authority={actions}><button type="button" title={project.path} aria-pressed={selectedId === project.id} onClick={() => onSelect(project.id)}>
        <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}</strong><small title={project.path}>{project.path}</small>{project.archived && <small>{t("Archived")}</small>}</span>
      </button>{canRename && selectedId === project.id && !project.archived && <button type="button" className="quiet-button"
        aria-label={t("Rename project {name} (F2)", { name: project.name })} disabled={renameBusy}
        onClick={onRename}>{t("Rename project (F2)")}</button>}</ProjectRowActions>)}
    </ul>
    <ul className="nav-list project-root-list" aria-label={t("Other sessions")}>
      <li><button type="button" aria-pressed={selectedId === null} onClick={() => onSelect(null)}>
        <span className="project-icon muted">◇</span><span><strong>{t("Other sessions")}</strong><small>{t("No matching project")}</small></span>
      </button></li>
    </ul>
  </>;
}
