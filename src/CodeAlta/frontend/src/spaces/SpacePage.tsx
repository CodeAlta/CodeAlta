import { useEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent } from "react";
import { Button, Callout, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { brandColor } from "../brands";
import { settingsFailure, type SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { SpaceAvailable, type ResourcePage } from "./SpaceAvailable";
import type { SpaceProject } from "./SpaceDialog";
import { SpaceGeneral, SpaceProjects } from "./SpaceGeneral";
import { SpaceIdentity } from "./SpaceIdentity";
import { SpaceNewSessions } from "./SpaceNewSessions";
import type { ResourceLists } from "./spaceResources";
import type { SpaceSectionProps } from "./spaceSection";
import { spaceSections, spaceSectionStatus, spaceSectionTitles, type SpaceSection } from "./spaceSummary";
import { SpaceTools } from "./SpaceTools";
import { SpaceActivityMarks, SpaceIcon } from "./SpaceViews";
import type { Space, SpaceActivity } from "./spaces";
import type { SpaceOutcome, SpacesHub } from "./spacesHub";

/**
 * The page of one space in Settings > Spaces: what it is (General), its projects, and what it sets up for its
 * sessions, each section a component of its own. The sections are tabs, each with what it sets in a few words, so
 * the page reads at a glance; one section shows at a time. Back returns to the cards of every space.
 */
export function SpacePage({ hub, space, spaces, projects, chats, shown, activity, canEdit, section, onSection, lists, onListsChanged, onOpenPage, onBack, onShow, onDeleted }: {
  hub: SpacesHub; space: Space; spaces: readonly Space[];
  /** Every project of the catalog. */
  projects: readonly SpaceProject[];
  /** How many chats the space has: deleting it moves them to the default space. */
  chats: number;
  /** Whether the window shows this space. */
  shown: boolean;
  activity: SpaceActivity | undefined;
  /** Whether the host lets spaces be changed. */
  canEdit: boolean;
  /** The section shown, and the choice of another one. */
  section: SpaceSection; onSection: (section: SpaceSection) => void;
  /** The resources of the user, read once for Settings > Spaces, and what reads them again. */
  lists: ResourceLists; onListsChanged: () => void;
  /** Opens the Settings page of a kind of resource; absent where none can be opened. */
  onOpenPage?: (page: ResourcePage) => void;
  onBack: () => void; onShow: () => void; onDeleted: () => void;
}) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const alive = useRef(true);
  const tabs = useRef<HTMLDivElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  // Another space is another page: what was said of the last one is not said of it.
  useEffect(() => setNotice(null), [space.id]);

  async function run(change: () => Promise<SpaceOutcome>, said: boolean = false) {
    setBusy(true); setNotice(null);
    const outcome = await change();
    if (!alive.current) return outcome;
    setBusy(false);
    const failure = outcome.ok ? null : settingsFailure(outcome.status, outcome.message) ?? { key: "The operation did not complete." as const, intent: "danger" as const };
    setNotice(failure ?? (said ? { key: "Saved.", intent: "success" } : null));
    return outcome;
  }

  async function remove() {
    const outcome = await run(() => hub.remove(space.id));
    if (outcome.ok && alive.current) onDeleted();
  }

  // A window whose host keeps no spaces has nothing to set up besides the name and the projects.
  const offered: readonly SpaceSection[] = state.available ? spaceSections : spaceSections.filter(item => item === "general" || item === "projects");
  const current = offered.includes(section) ? section : "general";
  const projectCount = space.isDefault ? projects.length : space.projectIds.length;
  const props: SpaceSectionProps = { space, spaces, hub, canEdit, busy, run };
  // Left and Right move between the sections, as in a list of tabs.
  function moveTab(event: KeyboardEvent<HTMLDivElement>) {
    const at = offered.indexOf(current);
    const next = event.key === "ArrowRight" ? at + 1 : event.key === "ArrowLeft" ? at - 1 : event.key === "Home" ? 0 : event.key === "End" ? offered.length - 1 : null;
    if (next === null) return;
    event.preventDefault();
    const target = offered[(next + offered.length) % offered.length];
    onSection(target);
    tabs.current?.querySelector<HTMLElement>(`[data-space-section="${target}"]`)?.focus();
  }
  return <main className="configuration-page settings-editor space-page" aria-label={t("Space: {name}", { name: space.name })}
    style={{ "--space-color": brandColor(space.color) ?? "var(--line-strong)" } as CSSProperties}>
    <nav className="space-page-trail" aria-label={t("Spaces")}>
      <Button variant="minimal" size="small" className="space-page-back" icon={<AppIcon name="arrowLeft" size={14} />} text={t("Spaces")} onClick={onBack} />
    </nav>
    <header className="page-heading settings-editor-heading space-page-heading">
      <div className="space-page-title">
        <span className="space-page-icon"><SpaceIcon space={space} size={26} /></span>
        <div>
          <h1>{space.name}</h1>
          {space.description && <p>{space.description}</p>}
        </div>
      </div>
      <div className="settings-editor-actions">
        {busy && <ActivitySpinner size={14} />}
        <SpaceActivityMarks activity={activity} />
        <Button className="space-show" variant={shown ? "minimal" : "outlined"} intent={shown ? "primary" : undefined} disabled={shown}
          icon={<AppIcon name={shown ? "check" : "eye"} size={14} />} text={t(shown ? "Shown" : "Show")} onClick={onShow} />
        {!space.isDefault && <PopoverNext placement="bottom-end" content={<div className="provider-settings-confirm">
          <p>{chats === 0 ? t("Delete {name}? Its projects stay in the other spaces they are in.", { name: space.name })
            : chats === 1 ? t("Delete {name}? Its chat goes to the Default space, and its projects stay in their other spaces.", { name: space.name })
            : t("Delete {name}? Its {count} chats go to the Default space, and its projects stay in their other spaces.", { name: space.name, count: chats })}</p>
          <Button className="bp6-popover-dismiss" intent="danger" disabled={!canEdit || busy} onClick={() => void remove()}>{t("Delete")}</Button></div>}>
          <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={!canEdit || busy} text={t("Delete")} />
        </PopoverNext>}
      </div>
    </header>
    <div className="space-section-nav" role="tablist" aria-label={t("Sections of {name}", { name: space.name })} ref={tabs} onKeyDown={moveTab}>
      {offered.map(item => {
        const status = spaceSectionStatus(item, space, projectCount, state.assignments, lists, t);
        return <Button key={item} variant="minimal" className="space-section-tab" role="tab" id={`space-${space.id}-tab-${item}`} data-space-section={item}
          aria-selected={item === current} aria-controls={`space-${space.id}-panel`} tabIndex={item === current ? 0 : -1} active={item === current}
          title={status ? `${t(spaceSectionTitles[item])}: ${status}` : undefined} onClick={() => onSection(item)}>
          <span className="space-section-tab-title">{t(spaceSectionTitles[item])}</span>
          {status && <span className="space-section-tab-status">{status}</span>}
        </Button>;
      })}
    </div>
    {notice && <Callout intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>
      {t(notice.key)}{notice.detail && <div className="config-editor-diagnostic">{notice.detail}</div>}</Callout>}
    <div className="space-sections" role="tabpanel" id={`space-${space.id}-panel`} aria-labelledby={`space-${space.id}-tab-${current}`}>
      {current === "general" && <SpaceGeneral {...props} />}
      {current === "projects" && <SpaceProjects {...props} projects={projects} />}
      {current === "resources" && <SpaceAvailable {...props} lists={lists} onChanged={onListsChanged} onOpenPage={onOpenPage} />}
      {current === "newSessions" && <SpaceNewSessions {...props} />}
      {current === "tools" && <SpaceTools {...props} />}
      {current === "identity" && <SpaceIdentity {...props} />}
    </div>
  </main>;
}
