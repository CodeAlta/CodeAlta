import { useMemo, useState, useSyncExternalStore } from "react";
import { Button, Card, InputGroup, PopoverNext, Switch, Tab, Tabs, Tag } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { SpaceScopeChip } from "./SpaceChips";
import type { AvailableResource, ResourceLists } from "./spaceResources";
import type { SpaceSectionProps } from "./spaceSection";
import { inSpace, resourceSpaces, spacesWith, spacesWithout, type SpaceResourceKind } from "./spaces";

export type { AvailableResource } from "./spaceResources";

type Tabbed = SpaceResourceKind;
/** The Settings page where the resources of a kind are added. */
export type ResourcePage = "providers" | "mcp" | "skills" | "prompts" | "plugins";
/** The tabs of the section, with what an empty list says and the page that adds one. */
export const availableTabs: readonly { kind: Tabbed; title: MessageKey; none: MessageKey; add: MessageKey; page: ResourcePage }[] = [
  { kind: "provider", title: "Providers", none: "No provider yet", add: "Add a provider…", page: "providers" },
  { kind: "mcp", title: "MCP Servers", none: "No MCP server yet", add: "Add an MCP server…", page: "mcp" },
  { kind: "skill", title: "Skills", none: "No skill yet", add: "Add a skill…", page: "skills" },
  { kind: "prompt", title: "Agent prompts", none: "No agent prompt yet", add: "Add an agent prompt…", page: "prompts" },
  { kind: "plugin", title: "Plugins", none: "No plugin yet", add: "Add a plugin…", page: "plugins" },
];

/**
 * The section "Resources" of the page of a space: the providers, MCP servers, skills, agent prompts and plugins of the
 * user, each with the spaces it is in and a switch that puts it in this space or takes it out. A resource of a project
 * follows its project and is not listed. Taking a shared resource out of the space asks first beside its switch: the
 * other spaces keep it, and a space created later will not get it. A kind with nothing yet points to the page that adds one.
 */
export function SpaceAvailable({ space, spaces, hub, canEdit, busy, run, lists, onChanged, onOpenPage }: SpaceSectionProps & Readonly<{
  /** The resources of each kind, read once for Settings > Spaces. */
  lists: ResourceLists;
  /** Reads the resources again, after one was turned off from here. */
  onChanged: () => void;
  /** Opens the Settings page of a kind; absent where none can be opened. */
  onOpenPage?: (page: ResourcePage) => void;
}>) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [tab, setTab] = useState<Tabbed>("provider");
  const [filter, setFilter] = useState("");
  const [asking, setAsking] = useState<string | null>(null);
  const every = useMemo(() => spaces.map(item => item.id), [spaces]);
  if (!state.available) return null;
  const current = availableTabs.find(item => item.kind === tab)!;
  const wanted = filter.trim().toLowerCase();
  const rows = (lists[tab] ?? []).filter(row => !wanted || row.name.toLowerCase().includes(wanted) || row.key.toLowerCase().includes(wanted));
  const disabled = !canEdit || busy;
  const count = (kind: Tabbed) => (lists[kind] ?? []).filter(row => inSpace(resourceSpaces(state.assignments, kind, row.key), space.id)).length;

  function set(row: AvailableResource, on: boolean) {
    const assigned = resourceSpaces(state.assignments, tab, row.key);
    if (on) { void run(() => hub.assignResource(tab, row.key, spacesWith(assigned, space.id, every))); return; }
    // A shared resource, or one that would be in no space: the popover beside the switch says what happens.
    if (assigned.length === 0 || spacesWithout(assigned, space.id, every) === null) { setAsking(row.key); return; }
    void run(() => hub.assignResource(tab, row.key, spacesWithout(assigned, space.id, every)!));
  }

  function confirm(row: AvailableResource) {
    setAsking(null);
    const next = spacesWithout(resourceSpaces(state.assignments, tab, row.key), space.id, every);
    if (next) void run(() => hub.assignResource(tab, row.key, next));
  }

  async function turnOff(row: AvailableResource) {
    setAsking(null);
    const done = await row.turnOff!();
    if (done) onChanged();
    else void run(async () => ({ ok: false, status: "write_failed", message: null, space: null }));
  }

  const id = `space-${space.id}`;
  return <Card className="appearance-settings space-section space-available" aria-labelledby={`${id}-available`}>
    <h2 id={`${id}-available`}>{t("Resources")}</h2>
    <p className="settings-field-help space-available-rule">{t("Shared items are in every space. Turn one off here to keep it out of this space.")}</p>
    <div className="space-available-bar">
      <Tabs id={`${id}-available-tabs`} className="space-available-tabs" selectedTabId={tab} onChange={next => { setTab(next as Tabbed); setAsking(null); }}>
        {availableTabs.map(item => <Tab key={item.kind} id={item.kind} title={<>{t(item.title)}
          {lists[item.kind] && <span className="space-section-count">{count(item.kind)}</span>}</>} />)}
      </Tabs>
      {(lists[tab]?.length ?? 0) > 8 && <InputGroup type="search" size="small" value={filter} placeholder={t("Search")} aria-label={t("Search")}
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
    </div>
    <div className="space-resource-list" role="list" aria-label={t(current.title)}>
      {rows.map(row => {
        const assigned = resourceSpaces(state.assignments, tab, row.key);
        const here = inSpace(assigned, space.id);
        const alone = here && spacesWithout(assigned, space.id, every) === null;
        return <div key={row.key} role="listitem" className="space-resource-row" data-here={here || undefined}>
          <span className="space-resource-logo">{row.logo}</span>
          <span className="space-resource-name"><strong>{row.name}</strong>{row.detail && <small>{row.detail}</small>}</span>
          <span className="space-resource-tags"><Tag minimal round>{t(row.source)}</Tag>
            {!row.enabled && <Tag minimal round>{t("Disabled")}</Tag>}
            <SpaceScopeChip spaces={spaces} assigned={assigned} /></span>
          <PopoverNext placement="left" isOpen={asking === row.key} onInteraction={open => { if (!open) setAsking(null); }} content={
            <div className="provider-settings-confirm space-resource-confirm">
              {alone ? <>
                <p>{t("{name} is only in this space.", { name: row.name })}</p>
                {row.turnOff && row.enabled && <Button intent="danger" disabled={disabled} onClick={() => void turnOff(row)}>{t("Turn it off")}</Button>}
              </> : <>
                <p>{t("Turn off {name} here? It stays in the other spaces, and a new space will not get it.", { name: row.name })}</p>
                <Button intent="primary" disabled={disabled} onClick={() => confirm(row)}>{t("Turn off here")}</Button>
              </>}
            </div>}>
            <Switch className="space-resource-switch" checked={here} disabled={disabled} aria-label={t("Available in {name}", { name: space.name })}
              onChange={event => set(row, event.currentTarget.checked)} />
          </PopoverNext>
        </div>;
      })}
      {lists[tab] === null && <p className="bp6-text-muted">{t("The settings could not be read.")}</p>}
      {lists[tab] && lists[tab].length === 0 && <div className="space-resource-empty">
        <span>{t(current.none)}</span>
        {onOpenPage && <Button variant="minimal" size="small" intent="primary" className="space-resource-add" text={t(current.add)}
          onClick={() => onOpenPage(current.page)} />}
      </div>}
      {lists[tab] && lists[tab].length > 0 && rows.length === 0 && <p className="bp6-text-muted">{t("Nothing matches.")}</p>}
    </div>
  </Card>;
}
