import { useState } from "react";
import { Card, CardList, Switch, Tag, type Intent } from "@blueprintjs/core";
import { plugins, type PluginsEntry } from "#neoastra";
import { ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
import type { SettingsScope } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

// The plugins that ship with CodeAlta are enabled unless configuration says otherwise, and the host
// lists them only once configuration names them.
const builtIn: readonly { id: string; name: string; description: MessageKey }[] = [
  { id: "mcp", name: "MCP", description: "Connects Model Context Protocol servers and exposes their tools." },
  { id: "github", name: "GitHub", description: "GitHub issue picker for prompts, and the GitHub CLI when available." },
  { id: "statistics", name: "Statistics", description: "Per-turn and session statistics." },
];
const stateIntent: Record<string, Intent> = { Enabled: "success", Failed: "danger", Changed: "warning", Disabled: "none", Configured: "none" };

/** Settings page for plugins: one list with an enable switch per plugin. */
export function PluginSettings({ epoch, project, api = plugins }: { epoch: string | null; project: SettingsProject; api?: typeof plugins }) {
  const { t } = useShellLanguage();
  const projectId = project?.id ?? null;
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(
    options => api.list({ expectedEpoch: epoch, projectId }, options), `${epoch}:${projectId}`);
  const [scope, setScope] = useState<SettingsScope>("Global");
  const writeScope: SettingsScope = project ? scope : "Global";
  const listed = listing?.plugins ?? [];
  const rows: { id: string; name: string; description: string | null; enabled: boolean; entry: PluginsEntry | null; builtIn: boolean }[] = [
    ...builtIn.map(plugin => { const entry = listed.find(value => value.id === plugin.id) ?? null;
      return { id: plugin.id, name: plugin.name, description: t(plugin.description), enabled: entry?.enabled ?? true, entry, builtIn: true }; }),
    ...listed.filter(entry => !builtIn.some(plugin => plugin.id === entry.id))
      .map(entry => ({ id: entry.id, name: entry.name || entry.id, description: entry.description, enabled: entry.enabled, entry, builtIn: false })),
  ];
  const toggle = (id: string, enabled: boolean) => void mutate(() => api.setEnabled({ expectedEpoch: epoch, projectId, scope: writeScope, id, enabled }, { timeoutMilliseconds: 30000 }),
    "Saved. Plugin changes apply the next time CodeAlta starts.");

  return <SettingsPage className="plugin-settings" label={t("Plugins")} group="Extensions" title="Plugins" description="Extensions that add tools and integrations to CodeAlta."
    notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="plugin" title="Plugins unavailable" /> : <>
      {project && <div className="settings-editor-toolbar"><ScopeChoice value={scope} project={project} disabled={busy} onChange={setScope} /></div>}
      <CardList compact className="settings-editor-rows" aria-label={t("Plugins")}>
        {rows.map(row => <Card key={row.id}>
          <span className="settings-editor-name"><strong>{row.name}</strong><small>{row.description || row.id}</small></span>
          <span className="settings-editor-tags"><Tag minimal round>{t(row.builtIn ? "Built-in" : row.entry?.scope === "Project" ? "Project" : "User")}</Tag>
            {row.entry && row.entry.kind === "Source" && row.entry.state !== "Enabled" && row.entry.state !== "Disabled"
              && <Tag minimal round intent={stateIntent[row.entry.state] ?? "none"}>{row.entry.state}</Tag>}
            <Switch checked={row.enabled} disabled={busy} aria-label={t("Enable {name}", { name: row.name })} onChange={event => toggle(row.id, event.currentTarget.checked)} /></span>
        </Card>)}
      </CardList>
    </>}
  </SettingsPage>;
}
