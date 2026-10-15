// The spaces of a resource (a provider, an MCP server, a skill, an agent prompt, a plugin), as a chip on its row, and
// the choice of them in a small popover beside it. Every page of resources uses these two, and the page of a space
// shows the chip of each resource it lists.
import { Button, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { KeyboardEvent } from "react";
import { AppIcon } from "../AppIcon";
import { showToast } from "../appToaster";
import { optionAfterKey } from "../ColorSchemeSelect";
import { settingsFailure } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { SpaceIcon } from "./SpaceViews";
import { resourceSpaces, type ResourceAssignments, type Space, type SpaceResourceKind } from "./spaces";
import type { SpaceOutcome, SpacesHub, SpacesState } from "./spacesHub";

/**
 * What the pages of resources need to show and change the spaces of their rows; null while there is no space but the
 * default one, where saying it would tell nothing.
 */
export type ResourceSpaces = Readonly<{
  spaces: readonly Space[];
  assignments: ResourceAssignments;
  assign: (kind: SpaceResourceKind, key: string, spaces: readonly string[]) => Promise<SpaceOutcome>;
}> | null;

/** The spaces of the resources for the pages, from what the hub keeps. */
export function resourceSpacesOf(state: SpacesState, hub: Pick<SpacesHub, "assignResource">): ResourceSpaces {
  return state.available && state.spaces.length > 1 ? { spaces: state.spaces, assignments: state.assignments, assign: hub.assignResource } : null;
}

/** The most space icons a chip shows before it counts the others. */
const shownIcons = 3;

/**
 * What a click on a space of the popover makes of the spaces of a resource: from every space, that space alone; a
 * space it has goes, and the last one going leaves it in every space again; any other space joins.
 */
export function toggledSpaces(assigned: readonly string[], spaceId: string): readonly string[] {
  if (assigned.length === 0) return [spaceId];
  return assigned.includes(spaceId) ? assigned.filter(id => id !== spaceId) : [...assigned, spaceId];
}

/** The names of the spaces of a resource, in the order of the spaces: one that names no space is said by its identifier. */
export function spaceNames(spaces: readonly Space[], assigned: readonly string[]): readonly string[] {
  const known = spaces.filter(space => assigned.includes(space.id)).map(space => space.name);
  return [...known, ...assigned.filter(id => !spaces.some(space => space.id === id))];
}

/** The spaces of a resource: the icons of its spaces, or "All spaces". */
export function SpaceScopeChip({ spaces, assigned }: { spaces: readonly Space[]; assigned: readonly string[] }) {
  const { t } = useShellLanguage();
  if (assigned.length === 0) return <span className="space-scope" data-all="true" title={t("All spaces")}>
    <AppIcon name="space" size={12} /><span className="space-scope-name">{t("All spaces")}</span></span>;
  const listed = spaces.filter(space => assigned.includes(space.id));
  const names = spaceNames(spaces, assigned).join(", ");
  return <span className="space-scope" title={names} aria-label={names}>
    {listed.slice(0, shownIcons).map(space => <SpaceIcon key={space.id} space={space} size={13} />)}
    {listed.length === 1 && <span className="space-scope-name">{listed[0].name}</span>}
    {assigned.length > Math.min(listed.length, shownIcons) && <span className="space-scope-more">+{assigned.length - Math.min(listed.length, shownIcons)}</span>}
  </span>;
}

// Up, Down, Home and End move through the entries as they do in a native list.
function moveFocus(event: KeyboardEvent<HTMLUListElement>) {
  const items = Array.from(event.currentTarget.querySelectorAll<HTMLElement>(".bp6-menu-item:not(.bp6-disabled)"));
  const next = optionAfterKey(event.key, items.indexOf(document.activeElement as HTMLElement), items.length);
  if (next === null) return;
  event.preventDefault();
  items[next].focus();
}

/**
 * The chip of the spaces of a resource, which opens beside it the choice of every space or of some, the default
 * space among them. Each click saves at once.
 */
export function SpaceScopePicker({ spaces, assigned, name, disabled, onChange }: {
  spaces: readonly Space[]; assigned: readonly string[];
  /** The name of the resource, for what a screen reader says. */
  name: string;
  disabled?: boolean;
  onChange: (next: readonly string[]) => void;
}) {
  const { t } = useShellLanguage();
  const label = t("Spaces of {name}", { name });
  return <span className="space-scope-picker" onClick={event => event.stopPropagation()} onKeyDown={event => event.stopPropagation()}>
    <PopoverNext placement="bottom-end" disabled={disabled} content={
      <Menu className="space-scope-menu" aria-label={label} onKeyDown={moveFocus}>
        <MenuItem roleStructure="listoption" selected={assigned.length === 0} shouldDismissPopover={false} icon={<AppIcon name="space" size={14} />}
          text={t("All spaces")} onClick={() => { if (assigned.length > 0) onChange([]); }} />
        <MenuDivider />
        {spaces.map(space => <MenuItem key={space.id} roleStructure="listoption" selected={assigned.includes(space.id)} shouldDismissPopover={false}
          icon={<SpaceIcon space={space} size={14} />} text={space.name} onClick={() => onChange(toggledSpaces(assigned, space.id))} />)}
      </Menu>}>
      <Button variant="minimal" size="small" className="space-scope-button" disabled={disabled} aria-label={label} title={label}
        endIcon={<AppIcon name="chevronDown" size={12} />}>
        <SpaceScopeChip spaces={spaces} assigned={assigned} />
      </Button>
    </PopoverNext>
  </span>;
}

/**
 * The spaces of a row of a page of resources, which the user changes beside it. A resource of a project follows its
 * project: it shows no chip.
 */
export function ResourceSpaceChip({ scope, kind, resourceKey, name, disabled }: {
  scope: ResourceSpaces; kind: SpaceResourceKind; resourceKey: string; name: string; disabled?: boolean;
}) {
  const { t } = useShellLanguage();
  if (!scope) return null;
  const assigned = resourceSpaces(scope.assignments, kind, resourceKey);
  async function change(next: readonly string[]) {
    const outcome = await scope!.assign(kind, resourceKey, next);
    if (outcome.ok) return;
    const failure = settingsFailure(outcome.status, outcome.message);
    showToast({ intent: failure?.intent ?? "danger", message: failure?.detail ?? t(failure?.key ?? "The operation did not complete.") }, `space-scope-${kind}-${resourceKey}`);
  }
  return <SpaceScopePicker spaces={scope.spaces} assigned={assigned} name={name} disabled={disabled} onChange={next => void change(next)} />;
}
