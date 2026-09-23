import type { WorkspaceProject } from "#neoastra";

export function ProjectRailRows({ projects, selectedId, onSelect, canRename, renameBusy, onRename }: {
  projects: WorkspaceProject[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  canRename: boolean;
  renameBusy: boolean;
  onRename: () => void;
}) {
  return <>
    <ul id="project-list" className="nav-list project-list" aria-label="Projects">
      {projects.map(project => <li key={project.id}><button type="button" title={project.path} aria-pressed={selectedId === project.id} onClick={() => onSelect(project.id)}>
        <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}</strong><small title={project.path}>{project.path}</small>{project.archived && <small>Archived</small>}</span>
      </button>{canRename && selectedId === project.id && !project.archived && <button type="button" className="quiet-button"
        aria-label={`Rename project ${project.name} (F2)`} disabled={renameBusy}
        onClick={onRename}>Rename project (F2)</button>}</li>)}
    </ul>
    <ul className="nav-list project-root-list" aria-label="Other sessions">
      <li><button type="button" aria-pressed={selectedId === null} onClick={() => onSelect(null)}>
        <span className="project-icon muted">◇</span><span><strong>Other sessions</strong><small>No matching project</small></span>
      </button></li>
    </ul>
  </>;
}
