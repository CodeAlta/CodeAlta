import { useContext, useEffect, useState } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, PopoverNext, Switch, Tag, type Intent } from "@blueprintjs/core";
import { plugins, projectFiles, type CanvasItem, type PluginsEntry, type PluginsProblem } from "#neoastra";
import { canvasScope } from "./canvases/canvasPages";
import { AppIcon } from "./AppIcon";
import { BrandIcon } from "./BrandIcon";
import { pluginBrand } from "./brands";
import { PluginButtonSwitches } from "./pluginButtons/PluginButtonSwitches";
import { PluginButtonsContext, readPluginButtons, type PluginButtonView } from "./pluginButtons/pluginButtonModel";
import { SettingsFileLocation, SettingsFileLocations, type SettingsFiles } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { RemoveButton, ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
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

/**
 * The rows of what the host lists: the plugins that ship with CodeAlta first, enabled until configuration says
 * otherwise. A source plugin that has the id of one of them keeps a row of its own, with its actions.
 */
export function pluginRows(listed: readonly PluginsEntry[], t: (key: MessageKey) => string): PluginRow[] {
  const source = (entry: PluginsEntry) => entry.kind === "Source";
  return [
    ...builtIn.map(plugin => { const entry = listed.find(value => value.id === plugin.id && !source(value)) ?? null;
      // Configuration has one switch per id: the package of that id says what it holds for the built-in one.
      const configured = entry?.enabled ?? listed.find(value => value.id === plugin.id)?.enabledGlobal ?? true;
      return { id: plugin.id, name: plugin.name, description: t(plugin.description), enabled: configured, entry, builtIn: true }; }),
    ...listed.filter(entry => source(entry) || !builtIn.some(plugin => plugin.id === entry.id))
      .map(entry => ({ id: entry.id, name: entry.name || entry.id, description: entry.description, enabled: entry.enabled, entry, builtIn: false })),
  ];
}

/**
 * The canvases a row's plugin declares: a built-in plugin is named by its row id in the key the host gives it, a source plugin by its folder.
 * A plugin that does not run declares none, so its row lists none.
 */
export function pluginCanvases(row: PluginRow, canvases: readonly CanvasItem[]): readonly CanvasItem[] {
  const folder = row.entry?.kind === "Source" && !row.builtIn ? row.entry.folder : null;
  return canvases.filter(canvas => row.builtIn ? canvas.pluginKey === `builtin:${row.id}` : !!folder && canvas.package === folder);
}

const problemText: Record<string, MessageKey> = {
  config: "The configuration file {path} could not be read.",
  folder: "The plugin folder {path} could not be read.",
  name: "{path} is not listed: the name of its folder is not a plugin id.",
  runtime: "The state of the running plugins could not be read.",
};

/**
 * What the host could not read while it listed the rest: each file or folder by its path, with what was said of it.
 * A configuration file that does not parse is opened in the code editor from here.
 */
export function PluginProblems({ problems, omitted, files }: { problems: readonly PluginsProblem[]; omitted: number; files?: SettingsFiles }) {
  const { t } = useShellLanguage();
  if (problems.length === 0 && omitted <= 0) return null;
  return <div className="plugin-problems" role="alert">
    {problems.map((problem, index) => <small key={index} className="plugin-failure">
      {t(problemText[problem.kind] ?? "The settings could not be read.", { path: problem.path ?? "" })}{problem.message ? ` ${problem.message}` : ""}
      {problem.kind === "config" && problem.path && problem.scope && files && <SettingsFileLocation path={problem.path} platform={files.platform}
        onOpen={files.open ? () => files.open!({ kind: "config", scope: problem.scope! }) : undefined} onReveal={() => files.reveal({ kind: "config", scope: problem.scope! })} />}</small>)}
    {omitted > 0 && <small className="plugin-failure">{t("{count} more are not listed.", { count: omitted })}</small>}
  </div>;
}

/**
 * The list of the page. A source plugin says what the running application did with it and what its last build
 * reported; it is built again in the running application while it is turned on, and opened in the code editor.
 */
export function PluginRows({ rows, disabled, onToggle, onReload, onEdit, onDelete, platform, onReveal, canvases = [], buttons }: {
  rows: readonly PluginRow[]; disabled: boolean;
  /** The canvases the running plugins declare: each row lists the ones of its plugin. */
  canvases?: readonly CanvasItem[];
  /** The buttons that running plugins put in the window: each plugin lists its own with a switch. */
  buttons?: readonly PluginButtonView[];
  onToggle: (id: string, enabled: boolean) => void; onReload: (entry: PluginsEntry) => void; onEdit?: (folder: PluginFolder) => void;
  /** Removes a source plugin: its folder goes to the trash. Without it the rows have no red button. */
  onDelete?: (entry: PluginsEntry) => void;
  /** The system, which names its file manager; null where a folder cannot be shown. */
  platform?: string | null;
  /** Shows the folder of a source plugin in the file manager. */
  onReveal?: (folder: PluginFolder) => void;
}) {
  const { t } = useShellLanguage();
  return <CardList compact className="settings-editor-rows" aria-label={t("Plugins")}>
    {rows.map(row => { const entry = row.entry; const source = entry?.kind === "Source" && !row.builtIn ? entry : null; const errors = entry?.errors ?? [];
      return <Card key={`${entry?.scope ?? ""}:${row.id}`}>
      <span className="settings-editor-logo">{pluginBrand(row.id) ? <BrandIcon name={pluginBrand(row.id)!} size={20} /> : <AppIcon name="plugin" size={18} />}</span>
      <span className="settings-editor-name"><strong>{row.name}</strong><small>{row.description || row.id}</small>
        {pluginCanvases(row, canvases).map(canvas => <small key={canvas.id} className="plugin-canvas">{t("Canvas: {title} ({scope})", { title: canvas.title, scope: t(canvasScope(canvas)) })}</small>)}
        {errors.map((error, index) => <small key={index} className="plugin-failure">{error}</small>)}
        {errors.length === 0 && entry?.runtime === "failed" && entry.runtimeMessage && <small className="plugin-failure">{entry.runtimeMessage}</small>}
        {source?.path && <SettingsFileLocation path={source.path} platform={platform}
          onReveal={source.folder && onReveal ? () => onReveal({ id: source.folder!, path: source.path!, name: source.id }) : undefined} />}
        {buttons && row.enabled && <PluginButtonSwitches buttons={buttons.filter(button => button.pluginId === row.id)} />}</span>
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
        {source && onDelete && <RemoveButton name={row.name} disabled={disabled} onRemove={() => onDelete(source)} />}
        <Switch checked={row.enabled} disabled={disabled} aria-label={t("Enable {name}", { name: row.name })} onChange={event => onToggle(row.id, event.currentTarget.checked)} /></span>
    </Card>; })}
  </CardList>;
}

/**
 * Settings page for plugins: one list with an enable switch per plugin. A source plugin is also opened in the
 * code editor, built again in the running application, and created from here.
 */
export function PluginSettings({ epoch, project, revision = 0, onEdit, onOpenFile, api = plugins, reveal = projectFiles.reveal, filesApi, canvases }: {
  epoch: string | null; project: SettingsProject;
  /** The canvases the running plugins declare, which each row lists. */
  canvases?: readonly CanvasItem[];
  /** Changes when the application started, replaced or stopped plugins: the list is read again. */
  revision?: number;
  /** Opens the code editor on the folder of a source plugin. */
  onEdit?: (folder: PluginFolder) => void;
  /** Called once the code editor was asked to show a plugin folder or a configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  api?: typeof plugins;
  /** Says where the files of the page are and opens them; the host by default. */
  filesApi?: SettingsFilesApi;
  /** Shows a file of a folder the host names in the file manager. */
  reveal?: typeof projectFiles.reveal;
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
  // The buttons that running plugins put in the window, to be shown or hidden here.
  const buttonsHost = useContext(PluginButtonsContext);
  const [buttons, setButtons] = useState<readonly PluginButtonView[]>([]);
  useEffect(() => {
    if (!epoch) { setButtons([]); return; }
    const abort = new AbortController();
    void buttonsHost.api.buttons({ expectedEpoch: epoch, place: null, spaceId: null, projectId, sessionId: null }, { signal: abort.signal, timeoutMilliseconds: 8000 })
      .then(reply => { if (!abort.signal.aborted) setButtons(readPluginButtons(reply) ?? []); }, () => { if (!abort.signal.aborted) setButtons([]); });
    return () => abort.abort();
  }, [buttonsHost.api, epoch, projectId, revision, listing]);
  const files = useSettingsFiles({ page: "plugins", epoch, projectId, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });
  const revealPlugin = (folder: PluginFolder) => void reveal({ expectedEpoch: epoch, projectId: folder.id, path: "" }, { timeoutMilliseconds: 15000 })
    .then(result => { if (result.status !== "ok") setNotice({ key: "The file manager could not be opened.", intent: "warning" }); },
      () => setNotice({ key: "The file manager could not be opened.", intent: "warning" }));
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
  const remove = (entry: PluginsEntry) => void change(
    () => api.delete({ expectedEpoch: epoch, projectId, scope: entry.scope === "Project" ? "Project" : "Global", id: entry.id }, { timeoutMilliseconds: 120000 }),
    () => "Removed.");
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
      <SettingsFileLocations files={files} disabled={disabled} />
      <PluginProblems problems={listing.problems ?? []} omitted={listing.omitted} files={files} />
      <PluginRows rows={rows} canvases={canvases} disabled={disabled} onToggle={toggle} onReload={rebuild} onEdit={onEdit} onDelete={remove} platform={files.platform} onReveal={revealPlugin} buttons={buttons} />
    </>}
  </SettingsPage>;
}
