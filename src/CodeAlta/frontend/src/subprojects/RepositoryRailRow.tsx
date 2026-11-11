import { useRef, useState, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import type { RailLabelGroup } from "../explorer/projectRail";
import { SessionTabMenu, type SessionMenuEntry } from "../SessionTabMenu";
import { useShellLanguage } from "../shellLanguage";

/** What the title of a repository offers: its sub-projects, the file of its map, and an agent that sets it up. */
export type RepositoryRowActions = Readonly<{
  subProjects: (repositoryId: string) => void;
  setUp?: (repositoryId: string) => void;
}>;

/**
 * The title of a repository in the Explorer, the head of its group: its name, how many projects it lists (itself and
 * its sub-projects), what runs or waits in them, and a chevron that folds them. Its `⋯` opens its sub-projects.
 */
export function RepositoryRailRow({ group, folded, onFold, actions, activity }: {
  group: RailLabelGroup;
  folded: boolean;
  onFold: (folded: boolean) => void;
  actions?: RepositoryRowActions;
  activity?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const repository = group.repository!;
  const trigger = useRef<HTMLButtonElement>(null);
  const [menu, setMenu] = useState(false);
  const count = group.label.projectIds.length;
  const items: SessionMenuEntry[] = actions ? [
    { key: "subprojects", label: t("Sub-projects…"), icon: "folder", onSelect: () => actions.subProjects(repository.id) },
    ...(actions.setUp ? [{ key: "setup", label: t("Set up with an agent…"), icon: "assistant" as const, onSelect: () => actions.setUp!(repository.id) }] : []),
  ] : [];
  return <li className="project-label-row project-repository-row" data-depth={group.depth} data-repository={repository.id}
    onContextMenu={event => { if (actions && !(event.target as HTMLElement).closest("input, textarea, [role=menu], dialog")) { event.preventDefault(); setMenu(true); } }}>
    <button type="button" className="project-label-toggle" data-label={group.label.id} aria-expanded={!folded}
      title={`${repository.name}\n${repository.path}`} onClick={() => onFold(!folded)}>
      <span className="tree-twist" aria-hidden="true"><AppIcon name="chevronDown" size={12} className={folded ? "tree-chevron" : "tree-chevron expanded"} /></span>
      <span className="label-icon project-repository-icon"><AppIcon name="files" size={14} /></span>
      <strong className="project-label-name">{repository.name}</strong>
      <span className="project-label-count" aria-label={t(count === 1 ? "{count} project" : "{count} projects", { count })}>{count}</span>
      {activity}
    </button>
    {actions && <button type="button" className="icon-button label-row-action project-repository-subprojects" aria-label={t("Sub-projects of {name}", { name: repository.name })}
      title={t("Sub-projects…")} onClick={() => actions.subProjects(repository.id)}><AppIcon name="settings" size={14} /></button>}
    {actions && <button ref={trigger} type="button" className="icon-button label-row-action label-actions-trigger"
      aria-label={t("Actions for {title}", { title: repository.name })} aria-haspopup="menu" aria-expanded={menu} onClick={() => setMenu(value => !value)}>
      <AppIcon name="ellipsis" size={15} /></button>}
    {menu && trigger.current && <SessionTabMenu anchor={trigger.current} title={t("Repository actions")} container={document.body} current={() => true}
      onClose={() => queueMicrotask(() => setMenu(false))} items={items} />}
  </li>;
}
