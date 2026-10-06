import type { WorkspaceProject } from "#neoastra";
import { Fragment, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { ProjectRowActions, type ProjectRowAuthority } from "./ProjectRowActions";
import { SessionTabMenu } from "../SessionTabMenu";

/** What the Explorer remembers and shows of its scopes: which are open, which projects are favorites. */
export type ProjectTreeView = Readonly<{
  expanded: (id: string | null) => boolean;
  toggle: (id: string | null) => void;
  favorite: (id: string) => boolean;
  setFavorite: (project: WorkspaceProject, favorite: boolean) => void;
  /** The sessions of an open scope other than the selected one. */
  sessions: (id: string | null) => ReactNode;
  /** What follows the sessions of an open scope: its terminals. */
  after?: (id: string | null) => ReactNode;
}>;

/** A tab of a project that its row opens: the code editor, the changes. */
type ProjectTabs = Readonly<{ open: (id: string) => boolean; show: (project: WorkspaceProject) => void }>;

const rowSelector = ".project-action-row > button:first-child, .session-row > button:first-child";

/**
 * Arrow keys in the rows of the Explorer: up and down go from row to row, right opens a scope, left closes it
 * or goes from a session to its scope.
 */
function rowKey(event: KeyboardEvent<HTMLElement>, toggle: (id: string | null) => void) {
  const row = event.target instanceof HTMLElement && event.target.matches(rowSelector) ? event.target : null;
  if (!row || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.nativeEvent.isComposing) return;
  const rows = () => Array.from((row.closest(".project-rail") ?? event.currentTarget).querySelectorAll<HTMLElement>(rowSelector)).filter(value => value.offsetParent !== null);
  const scope = row.dataset.scope;
  const open = row.getAttribute("aria-expanded");
  let target: HTMLElement | null | undefined;
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    const all = rows();
    target = all[all.indexOf(row) + (event.key === "ArrowDown" ? 1 : -1)] ?? row;
  } else if (event.key === "Home") target = rows()[0];
  else if (event.key === "End") target = rows().at(-1);
  else if (event.key === "ArrowRight" && scope !== undefined && open === "false") toggle(scope || null);
  else if (event.key === "ArrowLeft" && scope !== undefined && open === "true") toggle(scope || null);
  else if (event.key === "ArrowLeft" && scope === undefined)
    target = row.closest(".project-session-branch")?.previousElementSibling?.querySelector<HTMLElement>(":scope > button:first-child");
  else return;
  event.preventDefault();
  target?.focus();
}

export function ProjectRailRows({ projects, favorites = 0, selectedId, onSelect, actions, children, activity, renaming, editor, changes, terminals, tree, head }: {
  activity?: (projectId: string | null) => ReactNode;
  /** The projects listed, the favorite ones first. */
  projects: WorkspaceProject[];
  /** How many of the projects, from the first, are favorites: they are listed under a title of their own. */
  favorites?: number;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  canRename: boolean;
  renameBusy: boolean;
  onRename: () => void;
  /** The rename form of the project being renamed, shown beside its row. */
  renaming?: { id: string; form: ReactNode };
  actions?: ProjectRowAuthority;
  /** The code editors of the projects: which are open, which hold unsaved edits, and how one is opened. */
  editor?: ProjectTabs & Readonly<{ unsaved: (project: WorkspaceProject) => boolean }>;
  /** The Changes tabs of the projects: which are open, and how one is opened. */
  changes?: ProjectTabs;
  /** The terminals of the projects: how many a project has, and how a new one is opened in its folder. */
  terminals?: Readonly<{ count: (id: string) => number; create: (project: WorkspaceProject) => void }>;
  /** What is open and what is a favorite. Without it only the selected scope is open, until its row closes it. */
  tree?: ProjectTreeView;
  /** The sessions of the selected scope. */
  children?: ReactNode;
  /** What stands between the chats and the projects: the title of the projects and their filter. */
  head?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const [folded, setFolded] = useState<{ id: string | null } | null>(null);
  const otherTrigger = useRef<HTMLButtonElement>(null);
  const [otherMenu, setOtherMenu] = useState(false);
  const sessions = actions?.sessions;
  const open = (id: string | null) => tree ? tree.expanded(id) : id === selectedId && folded?.id !== selectedId;
  function toggle(id: string | null) {
    if (tree) tree.toggle(id);
    else if (id === selectedId) setFolded(open(id) ? { id } : null);
  }
  // The row of the selected scope also opens and closes it; the owner opens a scope that becomes the selected one.
  function select(id: string | null) {
    if (id === selectedId) toggle(id); else setFolded(null);
    onSelect(id);
  }
  const twist = (id: string | null) => <span className="tree-twist" onClick={event => { event.stopPropagation(); toggle(id); }}>
    <AppIcon name="chevronDown" size={12} className={open(id) ? "tree-chevron expanded" : "tree-chevron"} /></span>;
  // The sessions of the selected scope stay in the page while it is closed: what is being typed there is kept.
  const branch = (id: string | null) => id === selectedId ? <li className="project-session-branch" hidden={!open(id)}>{children}{tree?.after?.(id)}</li>
    : tree && open(id) ? <li className="project-session-branch">{tree.sessions(id)}{tree.after?.(id)}</li> : null;
  const row = (project: WorkspaceProject) => {
    const favorite = !!tree?.favorite(project.id);
    const editing = !!editor?.open(project.id);
    const changing = !!changes?.open(project.id);
    const running = terminals?.count(project.id) ?? 0;
    return <Fragment key={project.id}><ProjectRowActions project={project} authority={actions}
      favorite={tree ? { value: favorite, set: value => tree.setFavorite(project, value) } : undefined}>
      <button type="button" title={`${project.name}\n${project.path}`} aria-pressed={selectedId === project.id} aria-expanded={open(project.id)}
        data-scope={project.id} onClick={() => select(project.id)}>
        {twist(project.id)}
        <span className="project-icon" data-file-tone={project.archived ? "muted" : "gold"}><AppIcon name={project.archived ? "archive" : open(project.id) ? "open" : "folder"} size={15} /></span>
        <strong>{project.name}</strong>{activity?.(project.id)}{project.archived && <small>{t("Archived")}</small>}
      </button>
      {tree && <button type="button" className="icon-button project-row-action project-favorite-trigger" data-on={favorite}
        aria-label={t(favorite ? "Remove {name} from favorites" : "Add {name} to favorites", { name: project.name })}
        title={t(favorite ? "Remove from favorites" : "Add to favorites")} onClick={() => tree.setFavorite(project, !favorite)}>
        <AppIcon name="star" size={14} /></button>}
      {changes && !project.archived && <button type="button" className="icon-button project-row-action project-changes-trigger" data-open={changing}
        aria-label={t("Changes of {name}", { name: project.name })} title={t(changing ? "Show the changes" : "Open the changes")} onClick={() => changes.show(project)}>
        <AppIcon name="changes" size={15} /></button>}
      {editor && !project.archived && <button type="button" className="icon-button project-row-action project-editor-trigger" data-open={editing}
        data-unsaved={editor.unsaved(project)} aria-label={t("Code editor of {name}", { name: project.name })}
        title={t(editing ? "Show the code editor" : "Open the code editor")} onClick={() => editor.show(project)}>
        <AppIcon name="code" size={15} /></button>}
      {terminals && !project.archived && <button type="button" className="icon-button project-row-action project-terminal-trigger" data-open={running > 0}
        aria-label={t("New terminal in {name}", { name: project.name })}
        title={running > 0 ? `${t("New terminal")} (${t(running === 1 ? "{count} terminal" : "{count} terminals", { count: running })})` : t("New terminal")}
        onClick={() => terminals.create(project)}>
        <AppIcon name="terminal" size={15} /></button>}
      {renaming?.id === project.id && renaming.form}
    </ProjectRowActions>{branch(project.id)}</Fragment>;
  };
  const title = (key: "Favorites" | "Other projects", icon: boolean) => <li className="project-section" role="presentation">
    {icon && <AppIcon name="star" size={11} />}<span>{t(key)}</span></li>;
  // The chats, the sessions of no project, come first: one row, closed until it is opened.
  return <>
    <ul className="nav-list project-root-list" aria-label={t("Chats")} onKeyDown={event => rowKey(event, toggle)}>
      <li className="project-action-row" onContextMenu={event => { if (sessions) { event.preventDefault(); setOtherMenu(true); } }}>
        <button type="button" aria-pressed={selectedId === null} aria-expanded={open(null)} data-scope="" onClick={() => select(null)}>
          {twist(null)}<span className="project-icon" data-file-tone="teal"><AppIcon name="chat" size={15} /></span><strong>{t("Chats")}</strong>{activity?.(null)}
        </button>
        {sessions && <button ref={otherTrigger} type="button" className="icon-button project-actions-trigger" aria-label={t("Actions for {title}", { title: t("Chats") })}
          aria-haspopup="menu" aria-expanded={otherMenu} onClick={() => setOtherMenu(value => !value)}><AppIcon name="ellipsis" size={16} /></button>}
        {sessions && otherMenu && otherTrigger.current && <SessionTabMenu anchor={otherTrigger.current} title={t("Chats")} container={document.body}
          current={() => true} onClose={() => queueMicrotask(() => setOtherMenu(false))}
          items={[
            { key: "create", label: t("New chat"), icon: "newSession", disabled: !sessions.canCreate(null), onSelect: () => sessions.create(null) },
            { key: "search", label: `${t("Search sessions")}…`, icon: "search", onSelect: () => sessions.search(null) },
            { key: "browse", label: t("Browse saved sessions"), icon: "browse", onSelect: () => sessions.browse(null) },
          ]} />}
      </li>
      {branch(null)}
    </ul>
    {head}
    <ul id="project-list" className="nav-list project-list" aria-label={t("Projects")} onKeyDown={event => rowKey(event, toggle)}>
      {favorites > 0 && title("Favorites", true)}
      {projects.slice(0, favorites).map(row)}
      {favorites > 0 && favorites < projects.length && title("Other projects", false)}
      {projects.slice(favorites).map(row)}
    </ul>
  </>;
}
