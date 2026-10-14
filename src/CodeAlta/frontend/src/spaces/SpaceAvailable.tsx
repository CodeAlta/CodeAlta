import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from "react";
import { Button, Card, InputGroup, PopoverNext, Switch, Tab, Tabs, Tag } from "@blueprintjs/core";
import { agentPrompts, globalConfig, mcpServers, plugins, skills } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ProviderIcon } from "../ProviderIcon";
import { BrandIcon } from "../BrandIcon";
import { namedBrand, pluginBrand } from "../brands";
import type { MessageKey } from "../localization";
import { pluginRows } from "../PluginSettings";
import { promptHasSpaces } from "../PromptRows";
import { projectSkillSource, skillSourceLabels, skillToggleable } from "../SkillSettings";
import { useShellLanguage } from "../shellLanguage";
import { SpaceScopeChip } from "./SpaceChips";
import type { SpaceSectionProps } from "./spaceSection";
import { inSpace, resourceSpaces, spacesWith, spacesWithout, type SpaceResourceKind } from "./spaces";

/** A resource of the user that the section lists, whichever its kind. */
export type AvailableResource = Readonly<{
  key: string; name: string; detail: string | null; source: MessageKey;
  /** Whether its own switch, on its page, turns it on. */
  enabled: boolean;
  logo: ReactNode;
  /** Turns it off everywhere: what is offered when it would be in no space; absent for a resource that cannot be turned off. */
  turnOff?: () => Promise<boolean>;
}>;

type Tabbed = SpaceResourceKind;
/** The tabs of the section. */
export const availableTabs: readonly { kind: Tabbed; title: MessageKey }[] = [
  { kind: "provider", title: "Providers" }, { kind: "mcp", title: "MCP Servers" }, { kind: "skill", title: "Skills" }, { kind: "prompt", title: "Agent prompts" },
  { kind: "plugin", title: "Plugins" },
];
type Lists = Partial<Record<Tabbed, readonly AvailableResource[] | null>>;
const options = { timeoutMilliseconds: 15_000 };

// The resources of the user of each kind, from the pages that list them; null for a kind that could not be read.
async function readResources(epoch: string, t: (key: MessageKey) => string): Promise<Lists> {
  const read = async <T,>(call: () => Promise<T>) => { try { return await call(); } catch { return null; } };
  const [providers, servers, skillList, prompts, pluginList] = await Promise.all([
    read(() => globalConfig.providers({ expectedEpoch: epoch }, options)),
    read(() => mcpServers.list({ expectedEpoch: epoch, projectId: null }, options)),
    read(() => skills.list({ expectedEpoch: epoch, projectId: null }, options)),
    read(() => agentPrompts.list({ expectedEpoch: epoch, projectId: null }, options)),
    read(() => plugins.list({ expectedEpoch: epoch, projectId: null }, options)),
  ]);
  const ok = <T extends { status: string }>(reply: T | null): reply is T => reply?.status === "ok";
  return {
    // A provider is turned off on its own page, where its sessions are said.
    provider: !ok(providers) ? null : providers.providers.map(provider => ({
      key: provider.key, name: provider.effectiveName, detail: `${provider.key} · ${provider.type}`, source: "Global" as MessageKey, enabled: provider.enabled,
      logo: <ProviderIcon providerKey={provider.key} size={18} fallback="model"
        known={{ type: provider.type, name: provider.effectiveName, apiUrl: provider.effectiveApiUrl, icon: provider.icon, color: provider.color }} />,
    })),
    mcp: !ok(servers) ? null : servers.servers.filter(server => server.scope !== "Project").map(server => ({
      key: server.key, name: server.key, source: "Global" as MessageKey, enabled: server.enabled,
      detail: server.transport === "Http" ? server.url ?? "HTTP" : [server.command, ...server.arguments].filter(Boolean).join(" "),
      logo: namedBrand(server.key) ? <BrandIcon name={namedBrand(server.key)!} size={18} /> : <AppIcon name="server" size={16} />,
      turnOff: async () => (await mcpServers.setEnabled({ expectedEpoch: epoch, projectId: null, scope: server.scope, key: server.key, origin: server.origin, enabled: false }, options)).status === "ok",
    })),
    skill: !ok(skillList) ? null : skillList.skills.filter(skill => !projectSkillSource(skill.source) && skillToggleable(skill.name)).map(skill => ({
      key: skill.name, name: skill.title || skill.name, detail: skill.description, source: skillSourceLabels[skill.source] ?? "Plugin", enabled: skill.enabled,
      logo: <AppIcon name="skill" size={16} />,
      turnOff: async () => (await skills.setEnabled({ expectedEpoch: epoch, projectId: null, scope: "Global", name: skill.name, enabled: false }, options)).status === "ok",
    })),
    prompt: !ok(prompts) ? null : prompts.prompts.filter(prompt => promptHasSpaces(prompt) && !prompt.shadowed).map(prompt => ({
      key: prompt.id, name: prompt.name || prompt.id, detail: prompt.description || prompt.id, enabled: true,
      source: (prompt.scope === "BuiltIn" ? "Built-in" : "Global") as MessageKey, logo: <AppIcon name="assistant" size={16} />,
    })),
    plugin: !ok(pluginList) ? null : pluginRows(pluginList.plugins, t).filter(row => row.entry?.scope !== "Project").map(row => ({
      key: row.id, name: row.name, detail: row.description, source: (row.builtIn ? "Built-in" : "User") as MessageKey, enabled: row.enabled,
      logo: pluginBrand(row.id) ? <BrandIcon name={pluginBrand(row.id)!} size={18} /> : <AppIcon name="plugin" size={16} />,
      turnOff: async () => (await plugins.setEnabled({ expectedEpoch: epoch, projectId: null, scope: "Global", id: row.id, enabled: false }, { timeoutMilliseconds: 120_000 })).status === "ok",
    })),
  };
}

/**
 * The section "Available here" of the page of a space: the providers, MCP servers, skills, agent prompts and plugins of the
 * user, each with the spaces it is in and a switch that puts it in this space or takes it out. A resource of a project
 * follows its project and is not listed. Taking a shared resource out of the space asks first beside its switch: the
 * other spaces keep it, and a space created later will not get it.
 */
export function SpaceAvailable({ space, spaces, hub, canEdit, busy, run, read = readResources }: SpaceSectionProps & Readonly<{
  /** Reads the resources of each kind; the pages of Settings by default. */
  read?: (epoch: string, t: (key: MessageKey) => string) => Promise<Lists>;
}>) {
  const { t, locale } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const epoch = hub.hostEpoch();
  const [tab, setTab] = useState<Tabbed>("provider");
  const [lists, setLists] = useState<Lists>({});
  const [revision, setRevision] = useState(0);
  const [filter, setFilter] = useState("");
  const [asking, setAsking] = useState<string | null>(null);
  useEffect(() => {
    if (!epoch) return;
    let alive = true;
    void read(epoch, t).then(value => { if (alive) setLists(value); });
    return () => { alive = false; };
    // `t` is a new function at each render: the lists are read again when the language changes, not at each render.
  }, [epoch, revision, read, locale]);
  const every = useMemo(() => spaces.map(item => item.id), [spaces]);
  if (!state.available) return null;
  const wanted = filter.trim().toLowerCase();
  const rows = (lists[tab] ?? []).filter(row => !wanted || row.name.toLowerCase().includes(wanted) || row.key.toLowerCase().includes(wanted));
  const disabled = !canEdit || busy;
  const count = (kind: Tabbed) => (lists[kind] ?? []).filter(row => inSpace(resourceSpaces(state.assignments, kind, row.key), space.id)).length;

  function set(row: AvailableResource, on: boolean) {
    const assigned = resourceSpaces(state.assignments, tab, row.key);
    if (on) { void run(() => hub.assignResource(tab, row.key, spacesWith(assigned, space.id))); return; }
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
    if (done) setRevision(value => value + 1);
    else void run(async () => ({ ok: false, status: "write_failed", message: null, space: null }));
  }

  const id = `space-${space.id}`;
  return <Card className="appearance-settings space-section space-available" aria-labelledby={`${id}-available`}>
    <h2 id={`${id}-available`}>{t("Available here")}</h2>
    <div className="space-available-bar">
      <Tabs id={`${id}-available-tabs`} className="space-available-tabs" selectedTabId={tab} onChange={next => { setTab(next as Tabbed); setAsking(null); }}>
        {availableTabs.map(item => <Tab key={item.kind} id={item.kind} title={<>{t(item.title)}
          {lists[item.kind] && <span className="space-section-count">{count(item.kind)}</span>}</>} />)}
      </Tabs>
      {(lists[tab]?.length ?? 0) > 8 && <InputGroup type="search" size="small" value={filter} placeholder={t("Search")} aria-label={t("Search")}
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
    </div>
    <div className="space-resource-list" role="list" aria-label={t(availableTabs.find(item => item.kind === tab)!.title)}>
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
      {lists[tab] && rows.length === 0 && <p className="bp6-text-muted">{t(wanted ? "Nothing matches." : "Nothing to list yet.")}</p>}
    </div>
  </Card>;
}
