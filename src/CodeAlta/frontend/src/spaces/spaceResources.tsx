// The resources of the user that spaces are given: providers, MCP servers, skills, agent prompts and plugins, read
// once for Settings > Spaces. The cards of the overview sum them up, and the page of a space lists them.
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { agentPrompts, globalConfig, mcpServers, plugins, skills } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { BrandIcon } from "../BrandIcon";
import { namedBrand, pluginBrand } from "../brands";
import type { MessageKey } from "../localization";
import { pluginRows } from "../PluginSettings";
import { promptHasSpaces } from "../PromptRows";
import { ProviderIcon } from "../ProviderIcon";
import { projectSkillSource, skillSourceLabels, skillToggleable } from "../SkillSettings";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceResourceKind } from "./spaces";

/** A resource of the user that a space lists, whichever its kind. */
export type AvailableResource = Readonly<{
  key: string; name: string; detail: string | null; source: MessageKey;
  /** Whether its own switch, on its page, turns it on. */
  enabled: boolean;
  logo: ReactNode;
  /** Turns it off everywhere: what is offered when it would be in no space; absent for a resource that cannot be turned off. */
  turnOff?: () => Promise<boolean>;
}>;

/** The resources of each kind; null for a kind that could not be read, absent for one not read yet. */
export type ResourceLists = Partial<Record<SpaceResourceKind, readonly AvailableResource[] | null>>;

/** Reads the resources of each kind. */
export type ReadResources = (epoch: string, t: (key: MessageKey) => string) => Promise<ResourceLists>;

const options = { timeoutMilliseconds: 15_000 };

/** The resources of the user of each kind, from the pages that list them; null for a kind that could not be read. */
export const readResources: ReadResources = async (epoch, t) => {
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
};

/**
 * The resources of the user, read when a host connects and when the language changes, not at each render: the host
 * cuts off a page that asks too often. `reload` reads them again after a change made from the page.
 */
export function useResourceLists(epoch: string | null, read: ReadResources = readResources): Readonly<{ lists: ResourceLists; reload: () => void }> {
  const { t, locale } = useShellLanguage();
  const [lists, setLists] = useState<ResourceLists>({});
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    if (!epoch) return;
    let alive = true;
    void read(epoch, t).then(value => { if (alive) setLists(value); });
    return () => { alive = false; };
    // `t` is a new function at each render: the lists are read again when the language changes, not at each render.
  }, [epoch, revision, read, locale]);
  const reload = useCallback(() => setRevision(value => value + 1), []);
  return { lists, reload };
}
