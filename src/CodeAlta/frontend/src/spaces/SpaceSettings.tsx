import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type MouseEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Button, InputGroup, Switch } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { brandColor } from "../brands";
import { SettingsPage } from "../SettingsPage";
import { settingsFailure, type SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { useProjectDrag, type ProjectDrag } from "./projectDrag";
import type { SpaceProject } from "./SpaceDialog";
import type { ResourcePage } from "./SpaceAvailable";
import { SpacePage } from "./SpacePage";
import { readResources, useResourceLists, type ReadResources } from "./spaceResources";
import { spaceCardSummary, type SpacePageTarget } from "./spaceSummary";
import { SpaceActivityMarks, SpaceIcon } from "./SpaceViews";
import type { Space, SpaceActivity } from "./spaces";
import type { SpaceOutcome, SpacesHub } from "./spacesHub";

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

/** Whether a click on a card lands on nothing of its own (a button, a field, a project): it then opens the space. */
export function opensSpace(target: EventTarget | null, card: Element): boolean {
  return target instanceof Element && card.contains(target) && !target.closest("button, a, input, textarea, select, label, [data-movable=true], [role=group], .bp6-popover-target");
}

/**
 * The page of Settings that organizes the spaces: the list of every project, which is the default space, and
 * a card for each other space with its name, its icon, what it is for and its projects. A project is dragged
 * onto a space to join it, from a space to another to move (with Ctrl, to be in both), and back to the list to
 * leave. A click on a space opens its page, where it is named, described, given its projects and set up.
 */
export function SpaceSettings({ hub, spaces, projects, chats, shownId, activity, canEdit, showOthers, onShowOthers, onShow, onCreate, page, onPage, onOpenPage,
  read = readResources }: {
  hub: SpacesHub; spaces: readonly Space[];
  /** Every project of the catalog. */
  projects: readonly SpaceProject[];
  /** How many chats each space has, by its identifier. */
  chats: ReadonlyMap<string, number>;
  shownId: string; activity: ReadonlyMap<string, SpaceActivity>;
  /** Whether the host lets spaces be changed. */
  canEdit: boolean;
  /** Whether the default space shows what belongs to the other spaces too: a choice of the window. */
  showOthers: boolean; onShowOthers: (value: boolean) => void;
  onShow: (id: string) => void; onCreate: () => void;
  /** The space whose page is open, at which section; null for the cards of every space. */
  page: SpacePageTarget | null; onPage: (page: SpacePageTarget | null) => void;
  /** Opens the Settings page of a kind of resource. */
  onOpenPage?: (page: ResourcePage) => void;
  /** Reads the resources of the user; the pages of Settings by default. */
  read?: ReadResources;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [filter, setFilter] = useState("");
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  // The resources are read once for the cards and the pages: the host cuts off a page that asks too often.
  const { lists, reload } = useResourceLists(state.available ? hub.hostEpoch() : null, read);
  const openId = page?.spaceId ?? null;
  const setOpenId = (id: string | null) => onPage(id === null ? null : { spaceId: id, section: "general" });
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

  // A drop target is the identifier of a space, or the empty string for the list of every project.
  const drag = useProjectDrag((dragged, target, copy) => canEdit && !busy && !!dropChange(dragged, target || null, membersOf, copy), (dragged, target, copy) => {
    const change = canEdit && !busy ? dropChange(dragged, target || null, membersOf, copy) : null;
    if (change) void run(() => hub.assign(dragged.projectId, change.join, change.leave));
  });
  const dropTarget = (target: string | null) => ({ "data-space-drop": target ?? "", "data-over": drag.view?.over === (target ?? "") ? "true" : undefined });
  const dragSource = (project: SpaceProject, from: string | null) => drag.source(canEdit ? { projectId: project.id, from } : null, project.name);

  const open = openId === null ? undefined : spaces.find(space => space.id === openId);
  // A space deleted elsewhere closes its page; a page asked for before the spaces are read waits for them.
  useEffect(() => { if (openId !== null && !open && state.loaded) setOpenId(null); }, [openId, open, state.loaded]);
  if (open) {
    return <SpacePage hub={hub} space={open} spaces={spaces} projects={projects} chats={chats.get(open.id) ?? 0} shown={shownId === open.id} activity={activity.get(open.id)}
      canEdit={canEdit} section={page!.section} onSection={section => onPage({ spaceId: open.id, section })} lists={lists} onListsChanged={reload} onOpenPage={onOpenPage}
      onBack={() => setOpenId(null)} onShow={() => onShow(open.id)} onDeleted={() => setOpenId(null)} />;
  }

  const opener = (id: string) => ({ onClick: (event: MouseEvent<HTMLElement>) => { if (opensSpace(event.target, event.currentTarget)) setOpenId(id); } });
  // What a space sets up, on one line of its card: its projects, its resources by name, the email it works under.
  const summary = (space: (typeof spaces)[number], count: number) => {
    const parts = spaceCardSummary(space, count, state.assignments, lists, t).join(" · ");
    return <span className="space-card-count space-card-summary" title={parts}>{parts}</span>;
  };
  return <SettingsPage className="space-settings" label={t("Spaces")} group="Spaces" title="Spaces"
    description="Group your projects into spaces. The window shows one space at a time, and a project can be in several."
    notice={notice} loading={false} busy={busy} onReload={() => void hub.refresh()}
    actions={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!canEdit} onClick={onCreate}>{t("New space")}</Button>}>
    <div className="space-board">
      <section className="space-pool" aria-label={pool.name} {...dropTarget(null)} {...opener(pool.id)}>
        <SpaceCardHeading space={pool} activity={activity.get(pool.id)} onOpen={() => setOpenId(pool.id)} />
        <p className="space-pool-note">{t("Every project is here. Drag a project onto a space to add it.")}</p>
        {others.length > 0 && <Switch className="space-show-others" checked={showOthers} label={t("Show what belongs to other spaces")}
          onChange={event => onShowOthers(event.currentTarget.checked)} />}
        {projects.length > 8 && <InputGroup type="search" size="small" value={filter} placeholder={t("Search")} aria-label={t("Search")}
          leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
        <ul className="space-projects">
          {listed.map(project => <li key={project.id} className="space-project" data-archived={project.archived ? "true" : undefined} title={project.path} {...dragSource(project, null)}>
            {canEdit && <AppIcon name="grip" size={13} className="space-grip" />}
            <span className="space-project-name">{project.name}</span>
            <span className="space-project-spaces">{others.filter(space => space.projectIds.includes(project.id)).map(space =>
              <span key={space.id} title={space.name} role="img" aria-label={space.name}><SpaceIcon space={space} size={13} /></span>)}</span>
          </li>)}
          {listed.length === 0 && <li className="space-projects-empty">{t(projects.length ? "No project matches." : "No project yet.")}</li>}
        </ul>
        <footer>
          {summary(pool, projects.length)}
          <ShowSpace shown={shownId === pool.id} onShow={() => onShow(pool.id)} />
        </footer>
      </section>
      <div className="space-cards">
        {others.map(space => {
          const members = space.projectIds.map(id => byId.get(id)).filter(project => project !== undefined);
          const left = movedOrder(spaces, space.id, -1), right = movedOrder(spaces, space.id, 1);
          return <section key={space.id} className="space-card" aria-label={space.name} style={{ "--space-color": brandColor(space.color) ?? "var(--line-strong)" } as CSSProperties}
            {...dropTarget(space.id)} {...opener(space.id)}>
            <SpaceCardHeading space={space} activity={activity.get(space.id)} onOpen={() => setOpenId(space.id)}
              tools={<>
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowLeft" size={14} />} disabled={!canEdit || busy || !left}
                  aria-label={t("Move {name} before", { name: space.name })} title={t("Move before")} onClick={() => left && void run(() => hub.reorder(left))} />
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowRight" size={14} />} disabled={!canEdit || busy || !right}
                  aria-label={t("Move {name} after", { name: space.name })} title={t("Move after")} onClick={() => right && void run(() => hub.reorder(right))} />
              </>} />
            <ul className="space-projects">
              {members.map(project => <li key={project.id} className="space-project" data-archived={project.archived ? "true" : undefined} title={project.path} {...dragSource(project, space.id)}>
                {canEdit && <AppIcon name="grip" size={13} className="space-grip" />}
                <span className="space-project-name">{project.name}</span>
                <Button variant="minimal" size="small" className="space-project-remove" icon={<AppIcon name="close" size={13} />} disabled={!canEdit || busy}
                  aria-label={t("Remove {project} from {space}", { project: project.name, space: space.name })} title={t("Remove from this space")}
                  onClick={() => void run(() => hub.assign(project.id, [], [space.id]))} />
              </li>)}
              {members.length === 0 && <li className="space-projects-empty">{t("Drop projects here")}</li>}
            </ul>
            <footer>
              {summary(space, members.length)}
              <ShowSpace shown={shownId === space.id} onShow={() => onShow(space.id)} />
            </footer>
          </section>;
        })}
        <button type="button" className="space-card-new" disabled={!canEdit} onClick={onCreate}><AppIcon name="plus" size={18} /><span>{t("New space")}</span></button>
      </div>
      {drag.view && createPortal(<div className="space-drag-ghost" role="presentation" data-over={drag.view.over !== null ? "true" : undefined} data-copy={drag.view.copy ? "true" : undefined}
        style={{ left: drag.view.x + 14, top: drag.view.y + 10 }}><AppIcon name={drag.view.copy ? "plus" : "grip"} size={13} /><span>{drag.view.name}</span></div>, drag.view.layer)}
    </div>
  </SettingsPage>;
}

// Shows a space in the window, or says that it is the one shown.
function ShowSpace({ shown, onShow }: { shown: boolean; onShow: () => void }) {
  const { t } = useShellLanguage();
  return <Button size="small" className="space-show" variant={shown ? "minimal" : "outlined"} intent={shown ? "primary" : undefined} disabled={shown}
    icon={<AppIcon name={shown ? "check" : "eye"} size={13} />} text={t(shown ? "Shown" : "Show")} onClick={onShow} />;
}

// The head of a card: the icon and the name of the space, which open its page, and what it is for.
function SpaceCardHeading({ space, activity, onOpen, tools }: {
  space: Space; activity: SpaceActivity | undefined; onOpen: () => void; tools?: ReactNode;
}) {
  const { t } = useShellLanguage();
  return <header className="space-heading">
    <div className="space-heading-row">
      <button type="button" className="space-open" title={t("Open {name}", { name: space.name })} onClick={onOpen}>
        <SpaceIcon space={space} size={18} />
        <span className="space-open-name">{space.name}</span>
        <AppIcon name="chevronRight" size={14} className="space-open-chevron" />
      </button>
      <span className="space-heading-tools">
        <SpaceActivityMarks activity={activity} />
        {tools}
      </span>
    </div>
    {space.description && <p className="space-card-description">{space.description}</p>}
  </header>;
}
