import { useState } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, PopoverNext, Switch, Tag, type Intent } from "@blueprintjs/core";
import { plugins, type PluginsEntry } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
import { settingsFailure, type SettingsNotice, type SettingsScope } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

// The plugins that ship with CodeAlta are enabled unless configuration says otherwise, and the host
// lists them only once configuration names them.
const builtIn: readonly { id: string; name: string; description: MessageKey }[] = [
  { id: "mcp", name: "MCP", description: "Connects Model Context Protocol servers and exposes their tools." },
  { id: "git", name: "Git", description: "Issues and pull requests of GitHub, GitLab, Azure DevOps and Bitbucket repositories, and their CLIs when available." },
  { id: "jira", name: "Jira", description: "The issues of a Jira project, for the projects whose configuration names one." },
  { id: "statistics", name: "Statistics", description: "Per-turn and session statistics." },
  { id: "ui", name: "UI tools", description: "Tools an agent sees and drives this window with, when its session asks for them." },
];
const stateIntent: Record<string, Intent> = { Enabled: "success", Failed: "danger", Changed: "warning", Disabled: "none", Configured: "none" };
// What the running application did with an enabled plugin, when that is not simply "running".
const runtimeTag: Record<string, { label: MessageKey; intent: Intent }> = {
  unsupported: { label: "Not supported in the desktop application", intent: "none" },
  failed: { label: "Failed", intent: "danger" },
  stopped: { label: "Not started", intent: "warning" },
};
/** The folder of a source plugin, as the code editor opens it. */
export type PluginFolder = Readonly<{ id: string; path: string; name: string }>;
const pluginId = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/;

// Why a change of a plugin did not succeed, where the reason is one of plugins; the common reasons otherwise.
export function pluginFailure(status: string, message?: string | null): SettingsNotice | null {
  switch (status) {
    case "build_failed": return { key: "The plugin was not built. The version that was running keeps running.", intent: "danger", detail: message ?? null };
    case "start_failed": return { key: "The plugin was built and did not start.", intent: "danger", detail: message ?? null };
    case "not_loaded": return { key: "CodeAlta loads the plugins of the project it was started in.", intent: "warning" };
    case "disabled": return { key: "The plugin is turned off.", intent: "warning" };
    case "exists": return { key: "A plugin with this id already exists.", intent: "warning" };
    case "unknown": return settingsFailure("not_found");
    default: return settingsFailure(status, message);
  }
}

/** One row of the page: a plugin that ships with CodeAlta, or one that configuration or a plugin folder names. */
export type PluginRow = Readonly<{ id: string; name: string; description: string | null; enabled: boolean; entry: PluginsEntry | null; builtIn: boolean }>;

/** The rows of what the host lists: the plugins that ship with CodeAlta first, enabled until configuration says otherwise. */
export function pluginRows(listed: readonly PluginsEntry[], t: (key: MessageKey) => string): PluginRow[] {
  return [
    ...builtIn.map(plugin => { const entry = listed.find(value => value.id === plugin.id) ?? null;
      return { id: plugin.id, name: plugin.name, description: t(plugin.description), enabled: entry?.enabled ?? true, entry, builtIn: true }; }),
    ...listed.filter(entry => !builtIn.some(plugin => plugin.id === entry.id))
      .map(entry => ({ id: entry.id, name: entry.name || entry.id, description: entry.description, enabled: entry.enabled, entry, builtIn: false })),
  ];
}

/**
 * The list of the page. A source plugin says what the running application did with it and what its last build
 * reported; it is built again in the running application while it is turned on, and opened in the code editor.
 */
export function PluginRows({ rows, disabled, onToggle, onReload, onEdit }: {
  rows: readonly PluginRow[]; disabled: boolean;
  onToggle: (id: string, enabled: boolean) => void; onReload: (entry: PluginsEntry) => void; onEdit?: (folder: PluginFolder) => void;
}) {
  const { t } = useShellLanguage();
  return <CardList compact className="settings-editor-rows" aria-label={t("Plugins")}>
    {rows.map(row => { const entry = row.entry; const source = entry?.kind === "Source" && !row.builtIn ? entry : null; const errors = entry?.errors ?? [];
      return <Card key={`${entry?.scope ?? ""}:${row.id}`}>
      <span className="settings-editor-name"><strong>{row.name}</strong><small>{row.description || row.id}</small>
        {errors.map((error, index) => <small key={index} className="plugin-failure">{error}</small>)}
        {errors.length === 0 && entry?.runtime === "failed" && entry.runtimeMessage && <small className="plugin-failure">{entry.runtimeMessage}</small>}</span>
      <span className="settings-editor-tags"><Tag minimal round>{t(row.builtIn ? "Built-in" : entry?.scope === "Project" ? "Project" : "User")}</Tag>
        {entry && entry.kind === "Source" && entry.state !== "Enabled" && entry.state !== "Disabled"
          && <Tag minimal round intent={stateIntent[entry.state] ?? "none"}>{entry.state}</Tag>}
        {entry?.runtime && runtimeTag[entry.runtime] && !(entry.runtime === "failed" && entry.state === "Failed")
          && <Tag minimal round intent={runtimeTag[entry.runtime].intent}>{t(runtimeTag[entry.runtime].label)}</Tag>}
        {source?.changed && source.runtime === "running" && <Tag minimal round intent="warning">{t("Source changed")}</Tag>}
        {source?.loadable && row.enabled && <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={15} />} disabled={disabled}
          aria-label={t("Reload {name}", { name: row.name })} title={t("Build and reload")} onClick={() => onReload(source)} />}
        {source?.folder && source.path && onEdit && <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />}
          aria-label={t("Edit {name}", { name: row.name })} title={t("Edit in the code editor")}
          onClick={() => onEdit({ id: source.folder!, path: source.path!, name: source.id })} />}
        <Switch checked={row.enabled} disabled={disabled} aria-label={t("Enable {name}", { name: row.name })} onChange={event => onToggle(row.id, event.currentTarget.checked)} /></span>
    </Card>; })}
  </CardList>;
}

/**
 * Settings page for plugins: one list with an enable switch per plugin. A source plugin is also opened in the
 * code editor, built again in the running application, and created from here.
 */
export function PluginSettings({ epoch, project, revision = 0, onEdit, api = plugins }: {
  epoch: string | null; project: SettingsProject;
  /** Changes when the application started, replaced or stopped plugins: the list is read again. */
  revision?: number;
  /** Opens the code editor on the folder of a source plugin. */
  onEdit?: (folder: PluginFolder) => void;
  api?: typeof plugins;
}) {
  const { t } = useShellLanguage();
  const projectId = project?.id ?? null;
  const { listing, loading, busy, notice, setNotice, reload } = useSettingsEditor(
    options => api.list({ expectedEpoch: epoch, projectId }, options), `${epoch}:${projectId}:${revision}`);
  const [scope, setScope] = useState<SettingsScope>("Global");
  const [working, setWorking] = useState(false);
  const [draft, setDraft] = useState<{ id: string; description: string } | null>(null);
  const writeScope: SettingsScope = project ? scope : "Global";
  const disabled = busy || working;
  const listed = listing?.plugins ?? [];
  const rows = pluginRows(listed, t);
  // One change at a time; the list is read again after it, whether it succeeded or not.
  async function change<T extends { status: string; message?: string | null }>(call: () => Promise<T>, success: (result: T) => MessageKey | null) {
    setWorking(true); setNotice(null);
    try {
      const result = await call();
      const failure = pluginFailure(result.status, result.message);
      const done = failure ? null : success(result);
      setNotice(failure ?? (done ? { key: done, intent: "success" } : null));
      reload();
      return failure ? null : result;
    } catch { setNotice(settingsFailure("write_failed")); return null; }
    finally { setWorking(false); }
  }
  const toggle = (id: string, enabled: boolean) => void change(
    () => api.setEnabled({ expectedEpoch: epoch, projectId, scope: writeScope, id, enabled }, { timeoutMilliseconds: 120000 }),
    result => result.applied ? "Saved." : "Saved. Plugin changes apply the next time CodeAlta starts.");
  const rebuild = (entry: PluginsEntry) => void change(
    () => api.reload({ expectedEpoch: epoch, projectId, scope: entry.scope === "Project" ? "Project" : "Global", id: entry.id }, { timeoutMilliseconds: 180000 }),
    () => "Plugin reloaded.");
  const draftId = draft?.id.trim() ?? "";
  const draftProblem: MessageKey | null = !draft ? null : !pluginId.test(draftId) ? "Use letters, digits, dots, dashes or underscores for the plugin id."
    : listed.some(entry => entry.kind === "Source" && entry.scope === (writeScope === "Project" ? "Project" : "Global") && entry.id.toLowerCase() === draftId.toLowerCase())
      ? "A plugin with this id already exists." : null;
  async function create() {
    if (!draft || draftProblem || disabled) return;
    const created = await change(
      () => api.create({ expectedEpoch: epoch, projectId, scope: writeScope, id: draftId, description: draft.description.trim() || null }, { timeoutMilliseconds: 180000 }),
      () => "Plugin created.");
    if (!created) return;
    setDraft(null);
    if (created.folder && created.path) onEdit?.({ id: created.folder, path: created.path, name: created.name ?? draftId });
  }

  return <SettingsPage className="plugin-settings" label={t("Plugins")} group="Extensions" title="Plugins" description="Extensions that add tools and integrations to CodeAlta."
    notice={notice} loading={loading} busy={disabled} onReload={reload}
    actions={<PopoverNext placement="bottom-end" isOpen={!!draft} onInteraction={open => setDraft(open ? draft ?? { id: "", description: "" } : null)}
      content={<div className="settings-editor-popover">
        <FormGroup label={t("Plugin id")} labelFor="plugin-id"><InputGroup id="plugin-id" autoFocus value={draft?.id ?? ""} spellCheck={false} placeholder="my-plugin"
          onChange={event => setDraft(current => current && { ...current, id: event.target.value })} /></FormGroup>
        <FormGroup label={t("Description")} labelFor="plugin-description"><InputGroup id="plugin-description" value={draft?.description ?? ""}
          onChange={event => setDraft(current => current && { ...current, description: event.target.value })} /></FormGroup>
        {draftProblem && draft?.id && <span className="settings-editor-problem" role="alert">{t(draftProblem)}</span>}
        <Button intent="primary" disabled={!!draftProblem || disabled} onClick={() => void create()}>{t(writeScope === "Project" ? "Create in project" : "Create")}</Button>
      </div>}>
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!listing || disabled}>{t("New plugin")}</Button></PopoverNext>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="plugin" title="Plugins unavailable" /> : <>
      {project && <div className="settings-editor-toolbar"><ScopeChoice value={scope} project={project} disabled={disabled} onChange={setScope} /></div>}
      <PluginRows rows={rows} disabled={disabled} onToggle={toggle} onReload={rebuild} onEdit={onEdit} />
    </>}
  </SettingsPage>;
}
