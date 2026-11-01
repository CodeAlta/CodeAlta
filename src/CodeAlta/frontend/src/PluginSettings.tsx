import { useContext, useEffect, useState, type ReactNode } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, PopoverNext, Switch, Tag, type Intent } from "@blueprintjs/core";
import { ConfirmPopover } from "./ConfirmPopover";
import { plugins, projectFiles, type CanvasItem, type PluginsEntry, type PluginsProblem } from "#neoastra";
import { canvasPlacement, canvasScope, pluginSettingsPage, type PluginSettingsPage } from "./canvases/canvasPages";
import { AppIcon, type IconName } from "./AppIcon";
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
import { ResourceSpaceChip, type ResourceSpaces } from "./spaces/SpaceChips";

// The plugins that ship with CodeAlta are enabled unless configuration says otherwise, except the ones that are off until
// the user turns them on (`off`), and the host lists them only once configuration names them.
const builtIn: readonly { id: string; name: string; description: MessageKey; icon?: IconName; off?: true }[] = [
  { id: "mcp", name: "MCP", description: "Connects Model Context Protocol servers and exposes their tools." },
  { id: "git", name: "Git", description: "Issues and pull requests of GitHub, GitLab, Azure DevOps and Bitbucket repositories, and their CLIs when available." },
  { id: "jira", name: "Jira", description: "The issues of a Jira project, for the projects whose configuration names one." },
  { id: "statistics", name: "Statistics", description: "Per-turn and session statistics." },
  { id: "landing", name: "Landing page", description: "The welcome page: recent sessions and projects, the documentation and the cards of plugins." },
  { id: "ui", name: "UI tools", description: "Tools an agent sees and drives this window with, when its session asks for them." },
  { id: "acp", name: "ACP agents", description: "Coding agents of the Agent Client Protocol registry, as model providers." },
  // It publishes this computer on the tailnet: off until the user turns it on.
  { id: "mobile", name: "Mobile", description: "The sessions of this computer on a phone, through Tailscale.", icon: "smartphone", off: true },
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
export type PluginRow = Readonly<{ id: string; name: string; description: string | null; enabled: boolean; entry: PluginsEntry | null; builtIn: boolean; icon?: IconName }>;

/** What tells a row from the others: a source plugin of a project and one of the user can have the same id. */
export const pluginRowKey = (row: PluginRow) => `${row.entry?.scope ?? ""}:${row.id}`;

/**
 * The rows of what the host lists: the plugins that ship with CodeAlta first, enabled until configuration says
 * otherwise. A source plugin that has the id of one of them keeps a row of its own, with its actions.
 */
export function pluginRows(listed: readonly PluginsEntry[], t: (key: MessageKey) => string): PluginRow[] {
  const source = (entry: PluginsEntry) => entry.kind === "Source";
  return [
    ...builtIn.map(plugin => { const entry = listed.find(value => value.id === plugin.id && !source(value)) ?? null;
      // Configuration has one switch per id: the package of that id says what it holds for the built-in one. One that is off
      // until it is turned on follows the global switch alone, which is the one its host reads.
      const global = listed.find(value => value.id === plugin.id)?.enabledGlobal;
      const configured = plugin.off ? global ?? false : entry?.enabled ?? global ?? true;
      return { id: plugin.id, name: plugin.name, description: t(plugin.description), enabled: configured, entry, builtIn: true, icon: plugin.icon }; }),
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

/** The settings page of a row's plugin, in Settings > Extensions: its canvas of placement Settings, or null when it has none or does not run. */
export function pluginSettingsPageOf(row: PluginRow, canvases: readonly CanvasItem[]): PluginSettingsPage | null {
  const page = pluginCanvases(row, canvases).find(canvas => canvasPlacement(canvas) === "Settings");
  return page ? pluginSettingsPage(page) : null;
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
export function PluginRows({ rows, disabled, onToggle, onReload, onEdit, onDelete, platform, onReveal, canvases = [], buttons, spaceScope = null, onOpenSettings, question = null, typedSettings }: {
  rows: readonly PluginRow[]; disabled: boolean;
  /** A question about one row (stopping a plugin whose agents are at work), shown beside it: the key of the row and the question. */
  question?: Readonly<{ key: string; node: ReactNode }> | null;
  /** The spaces of the plugins of the user, which each row shows and changes; null with the default space alone. */
  spaceScope?: ResourceSpaces;
  /** The canvases the running plugins declare: each row lists the ones of its plugin. */
  canvases?: readonly CanvasItem[];
  /** The buttons that running plugins put in the window: each plugin lists its own with a switch. */
  buttons?: readonly PluginButtonView[];
  onToggle: (id: string, enabled: boolean, row: PluginRow) => void; onReload: (entry: PluginsEntry, row: PluginRow) => void; onEdit?: (folder: PluginFolder) => void;
  /** Removes a source plugin: its folder goes to the trash. Without it the rows have no red button. */
  onDelete?: (entry: PluginsEntry) => void;
  /** The system, which names its file manager; null where a folder cannot be shown. */
  platform?: string | null;
  /** Shows the folder of a source plugin in the file manager. */
  onReveal?: (folder: PluginFolder) => void;
  /** Goes to the settings page of a plugin; without it the rows have no gear button. */
  onOpenSettings?: (page: PluginSettingsPage) => void;
  /** The pages of the plugins that declare settings without a settings canvas, by the id of the plugin. */
  typedSettings?: ReadonlyMap<string, PluginSettingsPage>;
}) {
  const { t } = useShellLanguage();
  return <CardList compact className="settings-editor-rows" aria-label={t("Plugins")}>
    {rows.map(row => { const entry = row.entry; const source = entry?.kind === "Source" && !row.builtIn ? entry : null; const errors = entry?.errors ?? [];
      const settings = row.enabled && onOpenSettings ? pluginSettingsPageOf(row, canvases) ?? typedSettings?.get(row.id) ?? null : null;
      const key = pluginRowKey(row);
      return <Card key={key}>
      {question?.key === key && question.node}
      <span className="settings-editor-logo">{pluginBrand(row.id) ? <BrandIcon name={pluginBrand(row.id)!} size={20} /> : <AppIcon name={row.icon ?? "plugin"} size={18} />}</span>
      <span className="settings-editor-name"><strong>{row.name}</strong><small>{row.description || row.id}</small>
        {pluginCanvases(row, canvases).filter(canvas => canvasPlacement(canvas) === "Tab").map(canvas => <small key={canvas.id} className="plugin-canvas">{t("Canvas: {title} ({scope})", { title: canvas.title, scope: t(canvasScope(canvas)) })}</small>)}
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
          aria-label={t("Reload {name}", { name: row.name })} title={t("Build and reload")} onClick={() => onReload(source, row)} />}
        {source?.folder && source.path && onEdit && <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />}
          aria-label={t("Edit {name}", { name: row.name })} title={t("Edit in the code editor")}
          onClick={() => onEdit({ id: source.folder!, path: source.path!, name: source.id })} />}
        {source && onDelete && <RemoveButton name={row.name} disabled={disabled} onRemove={() => onDelete(source)} />}
        {entry?.scope !== "Project" && <ResourceSpaceChip scope={spaceScope} kind="plugin" resourceKey={row.id} name={row.name} disabled={disabled} />}
        {settings && <Button variant="minimal" size="small" icon={<AppIcon name="settings" size={15} />} aria-label={t("Settings of {name}", { name: row.name })}
          title={t("Settings of {name}", { name: row.name })} onClick={() => onOpenSettings?.(settings)} />}
        <Switch checked={row.enabled} disabled={disabled} aria-label={t("Enable {name}", { name: row.name })} onChange={event => onToggle(row.id, event.currentTarget.checked, row)} /></span>
    </Card>; })}
  </CardList>;
}

/**
 * Settings page for plugins: one list with an enable switch per plugin. A source plugin is also opened in the
 * code editor, built again in the running application, and created from here.
 */
export function PluginSettings({ epoch, project, revision = 0, onEdit, onOpenFile, api = plugins, reveal = projectFiles.reveal, filesApi, canvases, spaceScope = null, onOpenSettings, askBeforeStop = true, onStopQuestionDismissed, typedSettings }: {
  epoch: string | null; project: SettingsProject;
  /** Whether turning off or building again a plugin whose agents are at work asks first. */
  askBeforeStop?: boolean;
  /** The user asked not to be asked again. */
  onStopQuestionDismissed?: () => void;
  /** The spaces of the plugins of the user, which each row shows and changes; null with the default space alone. */
  spaceScope?: ResourceSpaces;
  /** The canvases the running plugins declare, which each row lists. */
  canvases?: readonly CanvasItem[];
  /** Goes to the settings page of a plugin, in Settings > Extensions. */
  onOpenSettings?: (page: PluginSettingsPage) => void;
  /** The pages of the plugins that declare settings without a settings canvas, by the id of the plugin. */
  typedSettings?: ReadonlyMap<string, PluginSettingsPage>;
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
  // A plugin whose providers have sessions at work (an agent of the ACP plugin) stops their turns when it stops or is
  // built again: the page asks first, beside its row.
  const [stopping, setStopping] = useState<Readonly<{ key: string; name: string; sessions: number; reload: boolean; go: () => void }> | null>(null);
  async function guard(row: PluginRow, reload: boolean, go: () => void) {
    if (!askBeforeStop || !epoch) { go(); return; }
    let sessions = 0;
    try {
      const reply = await api.providerSessions({ expectedEpoch: epoch, id: row.id }, { timeoutMilliseconds: 8000 });
      sessions = reply.status === "ok" ? reply.sessions : 0;
    } catch { /* Not known: nothing to ask about. */ }
    if (sessions > 0) setStopping({ key: pluginRowKey(row), name: row.name, sessions, reload, go });
    else go();
  }
  const setEnabled = (id: string, enabled: boolean) => void change(
    () => api.setEnabled({ expectedEpoch: epoch, projectId, scope: writeScope, id, enabled }, { timeoutMilliseconds: 120000 }),
    result => result.applied ? "Saved." : "Saved. Plugin changes apply the next time CodeAlta starts.");
  const toggle = (id: string, enabled: boolean, row?: PluginRow) => enabled || !row ? setEnabled(id, enabled) : void guard(row, false, () => setEnabled(id, enabled));
  const reloadEntry = (entry: PluginsEntry) => void change(
    () => api.reload({ expectedEpoch: epoch, projectId, scope: entry.scope === "Project" ? "Project" : "Global", id: entry.id }, { timeoutMilliseconds: 180000 }),
    () => "Plugin reloaded.");
  const rebuild = (entry: PluginsEntry, row?: PluginRow) => row ? void guard(row, true, () => reloadEntry(entry)) : reloadEntry(entry);
  const question = stopping && {
    key: stopping.key,
    node: <ConfirmPopover placement="bottom-end" title={t(stopping.reload ? "Build and reload {name}?" : "Turn off {name}?", { name: stopping.name })} subject={stopping.name}
      detail={stopping.sessions === 1 ? t("A session runs with its agents: its turn is stopped. It can go on later.")
        : t("{count} sessions run with its agents: their turns are stopped. They can go on later.", { count: stopping.sessions })}
      confirmLabel={t(stopping.reload ? "Reload" : "Turn off")} intent="warning" busy={false}
      onConfirm={remember => { if (remember) onStopQuestionDismissed?.(); const go = stopping.go; setStopping(null); go(); }}
      onCancel={() => setStopping(null)} />,
  };
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
    notice={notice} loading={loading} busy={disabled} onReload={() => { reload(); spaceScope?.refresh?.(); }}
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
      <PluginRows spaceScope={spaceScope} rows={rows} canvases={canvases} disabled={disabled || !!stopping} question={question} onToggle={toggle} onReload={rebuild} onEdit={onEdit} onDelete={remove} platform={files.platform} onReveal={revealPlugin} buttons={buttons} onOpenSettings={onOpenSettings} typedSettings={typedSettings} />
    </>}
  </SettingsPage>;
}
