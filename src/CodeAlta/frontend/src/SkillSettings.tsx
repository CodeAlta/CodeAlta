import { CopilotTag, copilotSkillSource } from "./CopilotTag";
import { useEffect, useMemo, useState } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, NonIdealState, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { projectFiles, skills, type SkillsDetailResponse, type SkillsEntry } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { skillReadOnly } from "./fileTabs";
import { MarkdownContent } from "./MarkdownContent";
import { PluginProblems } from "./PluginSettings";
import { skillInstructions } from "./skillDetail";
import { SettingsFileLocation, SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { RemoveButton, ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
import { settingsFailure, type SettingsScope } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const sourceLabels: Record<string, MessageKey> = { ProjectAlta: "Project", ProjectCommon: "Project", ProjectCopilot: "Project", UserAlta: "User", UserCommon: "User", UserCopilot: "User",
  Plugin: "Plugin", Builtin: "Built-in" };

const skillKey = (skill: Pick<SkillsEntry, "name" | "source">) => `${skill.source}:${skill.name}`;
/** Whether a skill is removed from here: one of a CodeAlta folder or of the common folder, of the user or of a project. */
export const skillRemovable = (source: string) => ["UserAlta", "ProjectAlta", "UserCommon", "ProjectCommon"].includes(source);

/** The folder of a skill, as the code editor opens on it: the id the host gave it, its path and the name of the skill. */
export type SkillFolder = Readonly<{ id: string; path: string; name: string }>;

/**
 * What the selected skill is, where it lives and what its SKILL.md tells the agent. Its folder opens in the code
 * editor: to be edited when the skill is one of the user or of a project, to be read otherwise.
 */
export function SkillDetail({ skill, detail, failed, onEdit, onDelete, disabled, platform, onReveal }: {
  skill: SkillsEntry; detail: SkillsDetailResponse | undefined; failed: boolean; onEdit?: (folder: SkillFolder) => void;
  /** Removes the skill: its folder goes to the trash. Without it, and for a skill that is not the user's to remove, there is no red button. */
  onDelete?: (skill: SkillsEntry) => void;
  disabled?: boolean;
  /** The system, which names its file manager; null where a file cannot be shown. */
  platform?: string | null;
  /** Shows the folder of the skill, or a file of it, in the file manager. */
  onReveal?: (folder: SkillFolder, path: string) => void;
}) {
  const { t } = useShellLanguage();
  const readOnly = skillReadOnly(skill.source);
  const facts: [MessageKey, string | null | undefined][] = !detail ? [] : [
    ["Overridden by", detail.shadowedBy], ["License", detail.license],
    ["Compatibility", detail.compatibility], ["Allowed tools", detail.allowedTools]];
  const folder = detail?.folder && detail.skillRootPath ? { id: detail.folder, path: detail.skillRootPath, name: skill.name } : null;
  // The folder of the skill and its SKILL.md: both open the code editor on that folder.
  const places: [MessageKey, string | null | undefined, string][] = !detail ? [] : [["Folder", detail.skillRootPath, ""], ["Skill file", detail.skillFilePath, "SKILL.md"]];
  const instructions = detail?.content ? skillInstructions(detail.content) : "";
  return <section className="skill-detail" aria-label={t("Details for {name}", { name: skill.title || skill.name })}>
    <header><h2>{skill.title || skill.name}</h2>
      <span className="settings-editor-tags"><Tag minimal round>{t(sourceLabels[skill.source] ?? "Plugin")}</Tag>{copilotSkillSource(skill.source) && <CopilotTag />}
        <Tag minimal round intent={skill.enabled ? "success" : "none"}>{t(skill.enabled ? "Enabled" : "Disabled")}</Tag>
        {detail && !detail.modelVisible && skill.enabled && <Tag minimal round intent="warning">{t("Not offered to the model")}</Tag>}
        {skill.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
        {!skill.valid && <Tag minimal round intent="danger">{t("Invalid")}</Tag>}
        {detail?.folder && detail.skillRootPath && onEdit && <Button size="small" icon={<AppIcon name="code" size={15} />}
          aria-label={t(readOnly ? "View the files of {name}" : "Edit {name}", { name: skill.title || skill.name })}
          title={t(readOnly ? "Open in the code editor" : "Edit in the code editor")}
          onClick={() => onEdit({ id: detail.folder!, path: detail.skillRootPath!, name: skill.name })}>{t(readOnly ? "View files" : "Edit")}</Button>}
        {onDelete && skillRemovable(skill.source) && <RemoveButton text name={skill.title || skill.name} disabled={disabled} onRemove={() => onDelete(skill)} />}</span></header>
    <p className="skill-detail-description">{skill.description}</p>
    {failed && <p role="alert" className="error-text">{t("The skill details could not be read.")}</p>}
    {detail && <>
      <dl className="skill-detail-facts">
        {places.filter(([, value]) => !!value).map(([label, value, path]) => <div key={label}><dt>{t(label)}</dt><dd>
          <SettingsFileLocation path={value!} readOnly={readOnly} platform={platform} onOpen={folder && onEdit ? () => onEdit(folder) : undefined}
            onReveal={folder && onReveal ? () => onReveal(folder, path) : undefined} /></dd></div>)}
        {facts.filter(([, value]) => !!value).map(([label, value]) => <div key={label}><dt>{t(label)}</dt><dd><code>{value}</code></dd></div>)}
        {detail.relatedFiles.length > 0 && <div><dt>{t("Related files")}</dt><dd>{detail.relatedFiles.map(file => <code key={file.path}>{file.path}</code>)}
          {detail.relatedFilesOmitted > 0 && <span className="bp6-text-muted">+{detail.relatedFilesOmitted}</span>}</dd></div>}
      </dl>
      {detail.diagnostics.length > 0 && <ul className="skill-detail-diagnostics" aria-label={t("Diagnostics")}>
        {detail.diagnostics.map((item, index) => <li key={index} data-severity={item.severity.toLowerCase()}><strong>{item.code}</strong> {item.message}</li>)}</ul>}
      {instructions ? <article className="skill-detail-instructions" aria-label="SKILL.md"><MarkdownContent source={instructions} document /></article>
        : <p className="bp6-text-muted">{t(detail.contentTruncated ? "The skill file is too large to show." : "The skill file has no instructions.")}</p>}
      {instructions && detail.contentTruncated && <p className="bp6-text-muted">{t("Only the beginning of the skill file is shown.")}</p>}
    </>}
  </section>;
}

/**
 * The list of the page: a row per skill with its switch, and the button that opens its folder in the code editor, to
 * be edited when the skill is one of the user or of a project, to be read otherwise.
 */
export function SkillRows({ skills, empty, selected, disabled, onSelect, onToggle, onEdit, onDelete }: {
  skills: readonly SkillsEntry[];
  /** What the list says when it has no skill. */
  empty: MessageKey;
  selected: SkillsEntry | undefined; disabled: boolean;
  onSelect: (skill: SkillsEntry) => void; onToggle: (skill: SkillsEntry, enabled: boolean) => void; onEdit?: (folder: SkillFolder) => void;
  /** Removes a skill of the user or of a project: its folder goes to the trash. Without it the rows have no red button. */
  onDelete?: (skill: SkillsEntry) => void;
}) {
  const { t } = useShellLanguage();
  return <CardList compact className="settings-editor-rows settings-editor-list" aria-label={t("Skills")}>
    {skills.map(skill => { const readOnly = skillReadOnly(skill.source), name = skill.title || skill.name;
      return <Card key={skillKey(skill)} interactive selected={skill === selected} aria-current={skill === selected ? "true" : undefined}
        onClick={event => { if (!(event.target as HTMLElement).closest(".bp6-switch, .bp6-button")) onSelect(skill); }}>
        <span className="settings-editor-name"><strong>{name}</strong><small>{skill.description}</small></span>
        <span className="settings-editor-tags"><Tag minimal round>{t(sourceLabels[skill.source] ?? "Plugin")}</Tag>{copilotSkillSource(skill.source) && <CopilotTag />}
          {skill.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
          {!skill.valid && <Tag minimal round intent="danger">{t("Invalid")}</Tag>}
          {skill.folder && skill.path && onEdit && <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />}
            aria-label={t(readOnly ? "View the files of {name}" : "Edit {name}", { name })} title={t(readOnly ? "Open in the code editor" : "Edit in the code editor")}
            onClick={() => onEdit({ id: skill.folder!, path: skill.path!, name: skill.name })} />}
          {onDelete && skillRemovable(skill.source) && <RemoveButton name={name} disabled={disabled} onRemove={() => onDelete(skill)} />}
          <Switch checked={skill.enabled} disabled={disabled} aria-label={t("Enable {name}", { name: skill.name })} onChange={event => onToggle(skill, event.currentTarget.checked)} /></span>
      </Card>; })}
    {skills.length === 0 && <Card><span className="bp6-text-muted">{t(empty)}</span></Card>}
  </CardList>;
}

/**
 * Settings page for skills: one list with an enable switch per skill, the selected skill's details, bulk actions and
 * skill creation. The folder of a skill is opened in the code editor, and so is the one of a skill that was created.
 */
export function SkillSettings({ epoch, project, onEdit, onOpenFile, api = skills, reveal = projectFiles.reveal, filesApi }: {
  epoch: string | null; project: SettingsProject;
  /** Opens the folder of a skill in the code editor. */
  onEdit?: (folder: SkillFolder) => void;
  /** Called once the code editor was asked to show a folder of skills: the window leaves Settings. */
  onOpenFile?: () => void;
  api?: typeof skills;
  /** Says where the files of the page are and opens them; the host by default. */
  filesApi?: SettingsFilesApi;
  /** Shows a file of a folder the host names in the file manager. */
  reveal?: typeof projectFiles.reveal;
}) {
  const { t } = useShellLanguage();
  const projectId = project?.id ?? null;
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(
    options => api.list({ expectedEpoch: epoch, projectId }, options), `${epoch}:${projectId}`);
  const [scope, setScope] = useState<SettingsScope>("Global");
  const [filter, setFilter] = useState("");
  const [draft, setDraft] = useState<{ name: string; description: string } | null>(null);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [detail, setDetail] = useState<{ key: string; value: SkillsDetailResponse | null }>();
  const writeScope: SettingsScope = project ? scope : "Global";
  const files = useSettingsFiles({ page: "skills", epoch, projectId, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });
  const revealSkill = (folder: SkillFolder, path: string) => void reveal({ expectedEpoch: epoch, projectId: folder.id, path }, { timeoutMilliseconds: 15000 })
    .then(result => { if (result.status !== "ok") setNotice({ key: "The file manager could not be opened.", intent: "warning" }); },
      () => setNotice({ key: "The file manager could not be opened.", intent: "warning" }));
  const all = listing?.skills ?? [];
  const shown = useMemo(() => {
    const text = filter.trim().toLowerCase();
    return text ? all.filter(skill => `${skill.name} ${skill.title} ${skill.description}`.toLowerCase().includes(text)) : all;
  }, [all, filter]);
  const selected = shown.find(skill => skillKey(skill) === selectedKey) ?? shown[0];
  const selectedName = selected?.name, selectedSource = selected?.source;
  useEffect(() => {
    if (!selectedName || !selectedSource) return;
    const key = skillKey({ name: selectedName, source: selectedSource });
    const abort = new AbortController();
    void api.detail({ expectedEpoch: epoch, projectId, name: selectedName, source: selectedSource }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(value => { if (!abort.signal.aborted) setDetail({ key, value: value.status === "ok" ? value : null }); },
        () => { if (!abort.signal.aborted) setDetail({ key, value: null }); });
    return () => abort.abort();
  }, [api, epoch, projectId, selectedName, selectedSource, listing]);
  const shownDetail = selected && detail?.key === skillKey(selected) ? detail : undefined;
  // The switch shows the effective state; a change is written to the chosen scope.
  const toggle = (skill: SkillsEntry, enabled: boolean) => void mutate(() => api.setEnabled({ expectedEpoch: epoch, projectId, scope: writeScope, name: skill.name, enabled }, { timeoutMilliseconds: 30000 }));
  const toggleAll = (enabled: boolean) => void mutate(() => api.setAllEnabled({ expectedEpoch: epoch, projectId, scope: writeScope,
    names: [...new Set(shown.map(skill => skill.name))], enabled }, { timeoutMilliseconds: 30000 }));
  const remove = (skill: SkillsEntry) => void mutate(() => api.delete({ expectedEpoch: epoch, projectId, name: skill.name, source: skill.source }, { timeoutMilliseconds: 120000 }), "Removed.");
  const draftProblem: MessageKey | null = !draft ? null : !/^[a-z0-9][a-z0-9-]{0,63}$/.test(draft.name.trim()) ? "Use lowercase letters, digits and dashes for the skill name."
    : all.some(skill => skill.name === draft.name.trim()) ? "A skill with this name already exists." : !draft.description.trim() ? "Describe when the skill should be used." : null;
  async function create() {
    if (!draft || draftProblem || busy) return;
    setNotice(null);
    try {
      const result = await api.create({ expectedEpoch: epoch, projectId, scope: writeScope, name: draft.name.trim(), description: draft.description.trim() }, { timeoutMilliseconds: 30000 });
      const failure = settingsFailure(result.status, result.message);
      setNotice(failure ?? { key: "Skill created. Edit its SKILL.md to add instructions.", intent: "success" });
      if (!failure) { setDraft(null); reload(); }
      if (!failure && result.folder && result.path) onEdit?.({ id: result.folder, path: result.path, name: result.name ?? draft.name.trim() });
    } catch { setNotice(settingsFailure("write_failed")); }
  }

  return <SettingsPage className="skill-settings" label={t("Skills")} group="Agent & models" title="Skills" description="Reusable instructions an agent loads when a task matches."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<PopoverNext placement="bottom-end" isOpen={!!draft} onInteraction={open => setDraft(open ? draft ?? { name: "", description: "" } : null)}
      content={<div className="settings-editor-popover">
        <FormGroup label={t("Name")} labelFor="skill-name"><InputGroup id="skill-name" autoFocus value={draft?.name ?? ""} spellCheck={false} placeholder="release-notes"
          onChange={event => setDraft(current => current && { ...current, name: event.target.value })} /></FormGroup>
        <FormGroup label={t("Description")} labelFor="skill-description"><InputGroup id="skill-description" value={draft?.description ?? ""}
          onChange={event => setDraft(current => current && { ...current, description: event.target.value })} /></FormGroup>
        {draftProblem && (draft?.name || draft?.description) && <span className="settings-editor-problem" role="alert">{t(draftProblem)}</span>}
        <Button intent="primary" disabled={!!draftProblem || busy} onClick={() => void create()}>{t(writeScope === "Project" ? "Create in project" : "Create")}</Button>
      </div>}>
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!listing || busy}>{t("New skill")}</Button></PopoverNext>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="skill" title="Skills unavailable" /> : <>
      {/* A configuration file that could not be read: the skills are listed as if it disabled none. */}
      <PluginProblems problems={listing.problems ?? []} omitted={0} files={files} />
      <SettingsFileLocations files={files} disabled={busy} />
      <div className="settings-editor-toolbar">
        <InputGroup className="settings-editor-filter" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={filter}
          placeholder={t("Filter skills")} aria-label={t("Filter skills")} onChange={event => setFilter(event.target.value)} />
        <ScopeChoice value={scope} project={project} disabled={busy} onChange={setScope} />
        <span className="settings-editor-spacer" />
        <span className="bp6-text-muted">{t("{enabled} of {total} enabled", { enabled: all.filter(skill => skill.enabled).length, total: all.length })}</span>
        <Button size="small" disabled={busy || shown.length === 0} onClick={() => toggleAll(true)}>{t("Enable all")}</Button>
        <Button size="small" disabled={busy || shown.length === 0} onClick={() => toggleAll(false)}>{t("Disable all")}</Button>
      </div>
      <div className="settings-editor-layout skill-settings-layout">
        <SkillRows skills={shown} empty={all.length ? "No skill matches the filter." : "No skills were found."} selected={selected} disabled={busy}
          onSelect={skill => setSelectedKey(skillKey(skill))} onToggle={toggle} onEdit={onEdit} onDelete={remove} />
        {selected ? <SkillDetail skill={selected} detail={shownDetail?.value ?? undefined} failed={shownDetail?.value === null} onEdit={onEdit} onDelete={remove} disabled={busy} platform={files.platform} onReveal={revealSkill} />
          : <NonIdealState icon={<AppIcon name="skill" size={32} />} title={t("No skills were found.")} />}
      </div>
    </>}
  </SettingsPage>;
}
