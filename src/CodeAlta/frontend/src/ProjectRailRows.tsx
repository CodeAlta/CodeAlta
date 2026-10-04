import type { WorkspaceProject } from "#neoastra";
import { Fragment, useRef, useState, type ReactNode } from "react";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import { ProjectRowActions, type ProjectRowAuthority } from "./ProjectRowActions";
import { SessionTabMenu } from "./SessionTabMenu";

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
  const otherTrigger = useRef<HTMLButtonElement>(null);
  const [otherMenu, setOtherMenu] = useState(false);
  const sessions = actions?.sessions;
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
    <ul className="nav-list project-root-list" aria-label={t("Global sessions")}>
      <li className="project-action-row" onContextMenu={event => { if (sessions) { event.preventDefault(); setOtherMenu(true); } }}>
        <button type="button" aria-pressed={selectedId === null} aria-expanded={selectedId === null && expanded} onClick={() => select(null)}>
          <AppIcon name="chevronDown" size={12} className={selectedId === null && expanded ? "tree-chevron expanded" : "tree-chevron"} /><AppIcon name="home" size={15} /><strong>{t("Global sessions")}</strong>{activity?.(null)}
        </button>
        {sessions && <button ref={otherTrigger} type="button" className="icon-button project-actions-trigger" aria-label={t("Actions for {title}", { title: t("Global sessions") })}
          aria-haspopup="menu" aria-expanded={otherMenu} onClick={() => setOtherMenu(value => !value)}><AppIcon name="ellipsis" size={16} /></button>}
        {sessions && otherMenu && otherTrigger.current && <SessionTabMenu anchor={otherTrigger.current} title={t("Sessions")} container={document.body}
          current={() => true} onClose={() => queueMicrotask(() => setOtherMenu(false))}
          items={[
            { key: "create", label: t("New session"), icon: "newSession", disabled: !sessions.canCreate(null), onSelect: () => sessions.create(null) },
            { key: "search", label: `${t("Search sessions")}…`, icon: "search", onSelect: () => sessions.search(null) },
            { key: "browse", label: t("Browse saved sessions"), icon: "browse", onSelect: () => sessions.browse(null) },
          ]} />}
      </li>
      {selectedId === null && <li className="project-session-branch" hidden={!expanded}>{children}</li>}
    </ul>
  </>;
}
