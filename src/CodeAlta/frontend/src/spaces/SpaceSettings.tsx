import { useEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent, type ReactNode } from "react";
import { Button, Checkbox, InputGroup, PopoverNext, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { brandColor } from "../brands";
import { ProviderIconPicker } from "../ProviderIconFields";
import { RemoveButton, SettingsPage } from "../SettingsPage";
import { settingsFailure, type SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceProject } from "./SpaceDialog";
import { SpaceActivityMarks, SpaceIcon } from "./SpaceViews";
import { maximumSpaceDescription, maximumSpaceName, spaceBrand, spaceColors, spaceNameProblem, type Space, type SpaceActivity } from "./spaces";
import type { SpaceOutcome, SpacesHub } from "./spacesHub";

/** What a drag of a project carries: the project, and the space it is dragged out of (null from the list of every project). */
export type ProjectDrag = Readonly<{ projectId: string; from: string | null }>;
/** The type a drag of a project has in the page: nothing else is dropped on a space. */
export const projectDragType = "application/x-codealta-space-project";

/**
 * What dropping a project does: on a space it joins it, and leaves the space it came from unless the drop is a
 * copy; on the list of every project it leaves the space it came from. Null for a drop that changes nothing.
 */
export function dropChange(drag: ProjectDrag, target: string | null, members: (spaceId: string) => readonly string[], copy: boolean):
  Readonly<{ join: readonly string[]; leave: readonly string[] }> | null {
  if (target === null) return drag.from === null ? null : { join: [], leave: [drag.from] };
  if (target === drag.from) return null;
  const joins = !members(target).includes(drag.projectId);
  const leaves = drag.from !== null && !copy;
  return joins || leaves ? { join: joins ? [target] : [], leave: leaves ? [drag.from!] : [] } : null;
}

/** The order of the spaces once one moved by one place; null when it is already at that end. */
export function movedOrder(spaces: readonly Space[], id: string, delta: 1 | -1): readonly string[] | null {
  const ids = spaces.filter(space => !space.isDefault).map(space => space.id);
  const at = ids.indexOf(id), to = at + delta;
  if (at < 0 || to < 0 || to >= ids.length) return null;
  [ids[at], ids[to]] = [ids[to], ids[at]];
  return ids;
}

/**
 * The page of Settings that organizes the spaces: the list of every project, which is the default space, and
 * a card for each other space with its name, its icon, its color, what it is for and its projects. A project
 * is dragged onto a space to join it, from a space to another to move (with Ctrl, to be in both), and back to
 * the list to leave. Everything is also done with the buttons of each card, and saved at once.
 */
export function SpaceSettings({ hub, spaces, projects, shownId, activity, canEdit, onShow, onCreate }: {
  hub: SpacesHub; spaces: readonly Space[];
  /** Every project of the catalog. */
  projects: readonly SpaceProject[];
  shownId: string; activity: ReadonlyMap<string, SpaceActivity>;
  /** Whether the host lets spaces be changed. */
  canEdit: boolean;
  onShow: (id: string) => void; onCreate: () => void;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [filter, setFilter] = useState("");
  const [over, setOver] = useState<string | null>(null);
  const drag = useRef<ProjectDrag | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const byId = useMemo(() => new Map(projects.map(project => [project.id, project])), [projects]);
  const others = spaces.filter(space => !space.isDefault);
  const pool = spaces.find(space => space.isDefault) ?? spaces[0];
  const wanted = filter.trim().toLowerCase();
  const listed = projects.filter(project => !wanted || project.name.toLowerCase().includes(wanted) || project.path.toLowerCase().includes(wanted));
  const membersOf = (id: string) => spaces.find(space => space.id === id)?.projectIds ?? [];

  async function run(change: () => Promise<SpaceOutcome>, said: boolean = false) {
    setBusy(true); setNotice(null);
    const outcome = await change();
    if (!alive.current) return outcome;
    setBusy(false);
    const failure = outcome.ok ? null : settingsFailure(outcome.status, outcome.message) ?? { key: "The operation did not complete." as const, intent: "danger" as const };
    setNotice(failure ?? (said ? { key: "Saved.", intent: "success" } : null));
    return outcome;
  }

  function accepts(target: string | null, event: DragEvent) {
    const dragged = drag.current;
    return !!dragged && canEdit && !busy && event.dataTransfer.types.includes(projectDragType) && !!dropChange(dragged, target, membersOf, event.ctrlKey || event.altKey);
  }
  const dropTarget = (target: string | null) => ({
    "data-over": over === (target ?? "") ? "true" : undefined,
    onDragOver: (event: DragEvent) => {
      if (!accepts(target, event)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = target !== null && (drag.current!.from === null || event.ctrlKey || event.altKey) ? "copy" : "move";
      if (over !== (target ?? "")) setOver(target ?? "");
    },
    onDragLeave: (event: DragEvent) => {
      if (event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)) return;
      setOver(current => current === (target ?? "") ? null : current);
    },
    onDrop: (event: DragEvent) => {
      const dragged = drag.current;
      const change = dragged && accepts(target, event) ? dropChange(dragged, target, membersOf, event.ctrlKey || event.altKey) : null;
      setOver(null); drag.current = null;
      if (!dragged || !change) return;
      event.preventDefault();
      void run(() => hub.assign(dragged.projectId, change.join, change.leave));
    },
  });
  const dragSource = (projectId: string, from: string | null) => canEdit ? {
    draggable: true,
    onDragStart: (event: DragEvent) => {
      drag.current = { projectId, from };
      event.dataTransfer.setData(projectDragType, projectId);
      event.dataTransfer.effectAllowed = from === null ? "copy" : "copyMove";
    },
    onDragEnd: () => { drag.current = null; setOver(null); },
  } : {};

  return <SettingsPage className="space-settings" label={t("Spaces")} group="Personalization" title="Spaces"
    description="Group your projects into spaces. The window shows one space at a time, and a project can be in several."
    notice={notice} loading={false} busy={busy} onReload={() => void hub.refresh()}
    actions={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!canEdit} onClick={onCreate}>{t("New space")}</Button>}>
    <div className="space-board">
      <section className="space-pool" aria-label={pool.name} {...dropTarget(null)}>
        <SpaceHeading space={pool} spaces={spaces} disabled={!canEdit || busy} activity={activity.get(pool.id)} save={edit => run(() => hub.update(pool.id, edit))} />
        <p className="space-pool-note">{t("Every project is here. Drag a project onto a space to add it.")}</p>
        {projects.length > 8 && <InputGroup type="search" size="small" value={filter} placeholder={t("Search")} aria-label={t("Search")}
          leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
        <ul className="space-projects">
          {listed.map(project => <li key={project.id} className="space-project" data-archived={project.archived ? "true" : undefined} title={project.path} {...dragSource(project.id, null)}>
            {canEdit && <AppIcon name="grip" size={13} className="space-grip" />}
            <span className="space-project-name">{project.name}</span>
            <span className="space-project-spaces">{others.filter(space => space.projectIds.includes(project.id)).map(space =>
              <span key={space.id} title={space.name} role="img" aria-label={space.name}><SpaceIcon space={space} size={13} /></span>)}</span>
          </li>)}
          {listed.length === 0 && <li className="space-projects-empty">{t(projects.length ? "No project matches." : "No project yet.")}</li>}
        </ul>
        <footer>
          <span className="space-card-count">{t(projects.length === 1 ? "1 project" : "{count} projects", { count: projects.length })}</span>
          <ShowSpace shown={shownId === pool.id} onShow={() => onShow(pool.id)} />
        </footer>
      </section>
      <div className="space-cards">
        {others.map(space => {
          const members = space.projectIds.map(id => byId.get(id)).filter(project => project !== undefined);
          const left = movedOrder(spaces, space.id, -1), right = movedOrder(spaces, space.id, 1);
          return <section key={space.id} className="space-card" aria-label={space.name} style={{ "--space-color": brandColor(space.color) ?? "var(--line-strong)" } as CSSProperties}
            {...dropTarget(space.id)}>
            <SpaceHeading space={space} spaces={spaces} disabled={!canEdit || busy} activity={activity.get(space.id)} save={edit => run(() => hub.update(space.id, edit))}
              tools={<>
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowLeft" size={14} />} disabled={!canEdit || busy || !left}
                  aria-label={t("Move {name} before", { name: space.name })} title={t("Move before")} onClick={() => left && void run(() => hub.reorder(left))} />
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowRight" size={14} />} disabled={!canEdit || busy || !right}
                  aria-label={t("Move {name} after", { name: space.name })} title={t("Move after")} onClick={() => right && void run(() => hub.reorder(right))} />
                <RemoveButton name={space.name} disabled={!canEdit || busy} onRemove={() => void run(() => hub.remove(space.id))} />
              </>} />
            <ul className="space-projects">
              {members.map(project => <li key={project.id} className="space-project" data-archived={project.archived ? "true" : undefined} title={project.path} {...dragSource(project.id, space.id)}>
                {canEdit && <AppIcon name="grip" size={13} className="space-grip" />}
                <span className="space-project-name">{project.name}</span>
                <Button variant="minimal" size="small" className="space-project-remove" icon={<AppIcon name="close" size={13} />} disabled={!canEdit || busy}
                  aria-label={t("Remove {project} from {space}", { project: project.name, space: space.name })} title={t("Remove from this space")}
                  onClick={() => void run(() => hub.assign(project.id, [], [space.id]))} />
              </li>)}
              {members.length === 0 && <li className="space-projects-empty">{t("Drop projects here")}</li>}
            </ul>
            <footer>
              <PopoverNext placement="bottom-start" content={<div className="space-add-list" role="group" aria-label={t("Projects of {name}", { name: space.name })}>
                {projects.filter(project => !project.archived || space.projectIds.includes(project.id)).map(project => <Checkbox key={project.id} disabled={busy}
                  checked={space.projectIds.includes(project.id)} title={project.path} label={project.name}
                  onChange={event => { const join = event.currentTarget.checked; void run(() => hub.assign(project.id, join ? [space.id] : [], join ? [] : [space.id])); }} />)}
                {projects.length === 0 && <p className="bp6-text-muted">{t("No project yet.")}</p>}
              </div>}>
                <Button size="small" variant="outlined" icon={<AppIcon name="plus" size={14} />} disabled={!canEdit} text={t("Projects")} />
              </PopoverNext>
              <span className="space-card-count">{t(members.length === 1 ? "1 project" : "{count} projects", { count: members.length })}</span>
              <ShowSpace shown={shownId === space.id} onShow={() => onShow(space.id)} />
            </footer>
          </section>;
        })}
        <button type="button" className="space-card-new" disabled={!canEdit} onClick={onCreate}><AppIcon name="plus" size={18} /><span>{t("New space")}</span></button>
      </div>
    </div>
  </SettingsPage>;
}

// Shows a space in the window, or says that it is the one shown.
function ShowSpace({ shown, onShow }: { shown: boolean; onShow: () => void }) {
  const { t } = useShellLanguage();
  return <Button size="small" className="space-show" variant={shown ? "minimal" : "outlined"} intent={shown ? "primary" : undefined} disabled={shown}
    icon={<AppIcon name={shown ? "check" : "eye"} size={13} />} text={t(shown ? "Shown" : "Show")} onClick={onShow} />;
}

// The head of a space: its icon, its name, its color and what it is for, each saved when it changes.
function SpaceHeading({ space, spaces, disabled, activity, save, tools }: {
  space: Space; spaces: readonly Space[]; disabled: boolean; activity: SpaceActivity | undefined;
  save: (edit: Readonly<{ name?: string; description?: string; icon?: string; color?: string }>) => Promise<SpaceOutcome>;
  tools?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(space.name);
  const [description, setDescription] = useState(space.description ?? "");
  useEffect(() => setName(space.name), [space.name]);
  useEffect(() => setDescription(space.description ?? ""), [space.description]);
  const problem = spaceNameProblem(name, spaces, space.id);
  const commitName = () => { if (problem || name.trim() === space.name) setName(space.name); else void save({ name: name.trim() }); };
  const commitDescription = () => { if (description.trim() !== (space.description ?? "")) void save({ description: description.trim() }); };
  return <header className="space-heading">
    <div className="space-heading-row">
      <ProviderIconPicker id={`space-icon-${space.id}`} compact value={space.icon ?? ""} automatic={{ icon: null }} shown={spaceBrand(space)} disabled={disabled}
        fallback="space" symbolsFirst onChange={icon => void save({ icon })} />
      <InputGroup className="space-name" value={name} disabled={disabled} maxLength={maximumSpaceName + 16} aria-label={t("Name")} spellCheck={false} autoComplete="off"
        intent={problem && name.trim() !== space.name ? "danger" : undefined} title={problem === "taken" ? t("A space already has this name.") : undefined}
        onChange={event => setName(event.target.value)} onBlur={commitName}
        onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); else if (event.key === "Escape") { event.stopPropagation(); setName(space.name); } }} />
      <span className="space-heading-tools">
        <SpaceActivityMarks activity={activity} />
        {tools}
      </span>
    </div>
    <div className="space-heading-row">
      <span className="space-color-swatches" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={disabled}
          aria-pressed={space.color?.toLowerCase() === color} aria-label={color} title={color} onClick={() => void save({ color: space.color?.toLowerCase() === color ? "" : color })} />)}
      </span>
    </div>
    <TextArea className="space-description" fill value={description} disabled={disabled} rows={1} maxLength={maximumSpaceDescription} aria-label={t("What it is for")}
      placeholder={t("What it is for")} onChange={event => setDescription(event.target.value)} onBlur={commitDescription} />
  </header>;
}
