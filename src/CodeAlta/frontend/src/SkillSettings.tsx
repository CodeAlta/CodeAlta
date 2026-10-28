import { CopilotTag, copilotSkillSource } from "./CopilotTag";
import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, NonIdealState, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { projectFiles, skills, type SkillsDetailResponse, type SkillsEntry, type SkillsMcpContentResponse, type SkillsMcpEntry, type SkillsMcpListResponse,
  type SkillsMcpServer } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { formatFileSize } from "./editor/editorDocuments";
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
import { ResourceSpaceChip, type ResourceSpaces } from "./spaces/SpaceChips";
import { useRowWindow } from "./rowWindow";
import { skillSourceLabels } from "./skillSources";

/** Where a skill comes from, as its tag says it. */
export { skillSourceLabels };

/** Whether a skill comes from a project, which it follows into the spaces of the project: it has no spaces of its own. */
export const projectSkillSource = (source: string) => source.startsWith("Project");

/** Whether a skill is removed from here: one of a CodeAlta folder or of the common folder, of the user or of a project. */
export const skillRemovable = (source: string) => ["UserAlta", "ProjectAlta", "UserCommon", "ProjectCommon"].includes(source);

/** Enablement is still name-based: malformed metadata is inspectable/removable, but cannot write an invalid slug. */
export const skillToggleable = (name: string) => {
  const normalized = name.trim().toLowerCase();
  return normalized.length <= 64 && /^[\p{L}\p{Nd}]+(?:-[\p{L}\p{Nd}]+)*$/u.test(normalized);
};

/** The folder of a skill, as the code editor opens on it: the id the host gave it, its path and the name of the skill. */
export type SkillFolder = Readonly<{ id: string; path: string; name: string }>;

/**
 * What the selected skill is, where it lives and what its SKILL.md tells the agent. Its folder opens in the code
 * editor: to be edited when the skill is one of the user or of a project, to be read otherwise.
 */
export function SkillDetail({ skill, detail, failed, onEdit, onDelete, disabled, platform, onReveal, spaceScope = null }: {
  skill: SkillsEntry; detail: SkillsDetailResponse | undefined; failed: boolean; onEdit?: (folder: SkillFolder) => void;
  /** The spaces of the skills of the user, which the header shows and changes; null with the default space alone. */
  spaceScope?: ResourceSpaces;
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
      <span className="settings-editor-tags"><Tag minimal round>{t(skillSourceLabels[skill.source] ?? "Plugin")}</Tag>{copilotSkillSource(skill.source) && <CopilotTag />}
        <Tag minimal round intent={skill.enabled ? "success" : "none"}>{t(skill.enabled ? "Enabled" : "Disabled")}</Tag>
        {detail && !detail.modelVisible && skill.enabled && <Tag minimal round intent="warning">{t("Not offered to the model")}</Tag>}
        {skill.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
        {!skill.valid && <Tag minimal round intent="danger">{t("Invalid")}</Tag>}
        {detail?.linked && <Tag minimal round title={t("This skill is reached through a symbolic link: edit or remove it in the folder the link leads to.")}>{t("Linked")}</Tag>}
        {!projectSkillSource(skill.source) && skillToggleable(skill.name)
          && <ResourceSpaceChip scope={spaceScope} kind="skill" resourceKey={skill.name} name={skill.title || skill.name} disabled={disabled} />}
        {detail?.folder && detail.skillRootPath && onEdit && <Button size="small" icon={<AppIcon name="code" size={15} />}
          aria-label={t(readOnly ? "View the files of {name}" : "Edit {name}", { name: skill.title || skill.name })}
          title={t(readOnly ? "Open in the code editor" : "Edit in the code editor")}
          onClick={() => onEdit({ id: detail.folder!, path: detail.skillRootPath!, name: skill.name })}>{t(readOnly ? "View files" : "Edit")}</Button>}
        {onDelete && skillRemovable(skill.source) && !detail?.linked && <RemoveButton text name={skill.title || skill.name} disabled={disabled} onRemove={() => onDelete(skill)} />}</span></header>
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
 * be edited when the skill is one of the user or of a project, to be read otherwise. A long list draws only the rows in
 * view (`useRowWindow`).
 */
export function SkillRows({ skills, empty, selected, disabled, onSelect, onToggle, onEdit, onDelete, spaceScope = null }: {
  skills: readonly SkillsEntry[];
  /** The spaces of the skills of the user, which each row shows and changes; null with the default space alone. */
  spaceScope?: ResourceSpaces;
  /** What the list says when it has no skill. */
  empty: MessageKey;
  selected: SkillsEntry | undefined; disabled: boolean;
  onSelect: (skill: SkillsEntry) => void; onToggle: (skill: SkillsEntry, enabled: boolean) => void; onEdit?: (folder: SkillFolder) => void;
  /** Removes a skill of the user or of a project: its folder goes to the trash. Without it the rows have no red button. */
  onDelete?: (skill: SkillsEntry) => void;
}) {
  const { t } = useShellLanguage();
  const keys = useMemo(() => skills.map(skill => skill.id), [skills]);
  const rows = useRowWindow(keys);
  const { start, end, before, after } = rows.slice;
  return <CardList compact className="settings-editor-rows settings-editor-list" aria-label={t("Skills")}>
    {rows.windowed && <div ref={rows.anchor} className="row-window-spacer" style={{ height: before }} aria-hidden="true" />}
    {skills.slice(start, end + 1).map(skill => { const readOnly = skillReadOnly(skill.source), name = skill.title || skill.name;
      return <Card key={skill.id} ref={rows.measure} data-row-key={skill.id} interactive selected={skill === selected} aria-current={skill === selected ? "true" : undefined}
        onClick={event => { if (!(event.target as HTMLElement).closest(".bp6-switch, .bp6-button")) onSelect(skill); }}>
        <span className="settings-editor-name"><strong>{name}</strong><small>{skill.description}</small></span>
        <span className="settings-editor-tags"><Tag minimal round>{t(skillSourceLabels[skill.source] ?? "Plugin")}</Tag>{copilotSkillSource(skill.source) && <CopilotTag />}
          {skill.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
          {!skill.valid && <Tag minimal round intent="danger">{t("Invalid")}</Tag>}
          {skill.folder && skill.path && onEdit && <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />}
            aria-label={t(readOnly ? "View the files of {name}" : "Edit {name}", { name })} title={t(readOnly ? "Open in the code editor" : "Edit in the code editor")}
            onClick={() => onEdit({ id: skill.folder!, path: skill.path!, name: skill.name })} />}
          {onDelete && skillRemovable(skill.source) && <RemoveButton name={name} disabled={disabled} onRemove={() => onDelete(skill)} />}
          {!projectSkillSource(skill.source) && skillToggleable(skill.name)
            && <ResourceSpaceChip scope={spaceScope} kind="skill" resourceKey={skill.name} name={name} disabled={disabled} />}
          <Switch checked={skill.enabled} disabled={disabled || !skillToggleable(skill.name)} aria-label={t("Enable {name}", { name })} onChange={event => onToggle(skill, event.currentTarget.checked)} /></span>
      </Card>; })}
    {rows.windowed && <div className="row-window-spacer" style={{ height: after }} aria-hidden="true" />}
    {skills.length === 0 && <Card><span className="bp6-text-muted">{t(empty)}</span></Card>}
  </CardList>;
}

/** The key of a skill of an MCP server in the selection of the page: its server and its address. */
export const mcpSkillKey = (skill: Pick<SkillsMcpEntry, "server" | "uri">) => `mcp:${skill.server}:${skill.uri}`;

/** What the user decided about the files of a skill of an MCP server, as its details say it. */
export const mcpApprovalLabels: Readonly<Record<string, MessageKey>> = Object.freeze({
  approved: "Approved", not_approved: "Not approved", changed: "Changed since it was approved", once_only: "Allowed once only" });

/**
 * The skills the MCP servers publish, below the local ones: a group per server, a row per skill with its switch. They
 * are read from their server: nothing here edits or removes them.
 */
export function McpSkillRows({ servers, selected, disabled, onSelect, onToggle }: {
  servers: readonly SkillsMcpServer[];
  /** The key of the selected skill (`mcpSkillKey`), null when a local skill is selected. */
  selected: string | null; disabled: boolean;
  onSelect: (skill: SkillsMcpEntry) => void; onToggle: (skill: SkillsMcpEntry, enabled: boolean) => void;
}) {
  const { t } = useShellLanguage();
  if (!servers.length) return null;
  return <CardList compact className="settings-editor-rows settings-editor-list skill-settings-mcp" aria-label={t("Skills from MCP servers")}>
    {servers.map(server => <Fragment key={server.server}>
      <h3 className="settings-editor-group">{t("From the MCP server {server}", { server: server.server })}</h3>
      {server.problems.map((problem, index) => <Card key={`problem:${index}`} className="skill-settings-mcp-problem"><span className="error-text">{problem}</span></Card>)}
      {server.skills.map(skill => { const key = mcpSkillKey(skill);
        return <Card key={key} data-mcp-skill={key} interactive selected={key === selected} aria-current={key === selected ? "true" : undefined}
          onClick={event => { if (!(event.target as HTMLElement).closest(".bp6-switch, .bp6-button")) onSelect(skill); }}>
          <span className="settings-editor-name"><strong>{skill.name}</strong><small>{skill.description}</small></span>
          <span className="settings-editor-tags"><Tag minimal round>{t("MCP · {server}", { server: skill.server })}</Tag>
            {skill.namedLikeLocal && <Tag minimal round intent="warning">{t("Named like a local skill")}</Tag>}
            <Switch checked={skill.enabled} disabled={disabled} aria-label={t("Enable {name} from {server}", { name: skill.name, server: skill.server })}
              onChange={event => onToggle(skill, event.currentTarget.checked)} /></span>
        </Card>; })}
    </Fragment>)}
  </CardList>;
}

/** Forgets that the user allowed a skill of an MCP server: its next load asks again. Asks first, beside the button. */
function ForgetApprovalButton({ name, disabled, onForget }: { name: string; disabled?: boolean; onForget: () => void }) {
  const { t } = useShellLanguage();
  return <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Forget the approval of {name}?", { name })}</p>
    <Button className="bp6-popover-dismiss" intent="danger" disabled={disabled} onClick={onForget}>{t("Forget approval")}</Button></div>}>
    <Button size="small" variant="minimal" disabled={disabled} data-mcp-forget>{t("Forget approval")}</Button>
  </PopoverNext>;
}

/**
 * What a skill of an MCP server is: its description, the fields of its `SKILL.md` (`allowed-tools`, which CodeAlta never
 * acts on, marked ignored), its files and whether the user allowed them. Its `SKILL.md` is read from the server on demand.
 */
export function McpSkillDetail({ skill, disabled, onForget, readContent }: {
  skill: SkillsMcpEntry; disabled?: boolean;
  /** Forgets the approval of the skill; without it, the details offer no such button. */
  onForget?: (skill: SkillsMcpEntry) => void;
  /** Reads the `SKILL.md` of the skill from its server; without it, the details offer no reading. */
  readContent?: (skill: SkillsMcpEntry, signal: AbortSignal) => Promise<SkillsMcpContentResponse>;
}) {
  const { t, locale } = useShellLanguage();
  const [content, setContent] = useState<{ value: SkillsMcpContentResponse | null } | "reading" | null>(null);
  const reading = useRef<AbortController | null>(null);
  useEffect(() => () => reading.current?.abort(), []);
  function toggleContent() {
    reading.current?.abort();
    if (content || !readContent) { reading.current = null; setContent(null); return; }
    const abort = reading.current = new AbortController();
    setContent("reading");
    readContent(skill, abort.signal).then(value => { if (!abort.signal.aborted) setContent({ value }); },
      () => { if (!abort.signal.aborted) setContent({ value: null }); });
  }
  const read = typeof content === "object" && content !== null ? content.value : undefined;
  const instructions = read?.status === "ok" && read.content !== null ? skillInstructions(read.content) : null;
  const fields = skill.frontmatter.filter(field => field.name !== "name" && field.name !== "description");
  return <section className="skill-detail skill-mcp-detail" aria-label={t("Details for {name}", { name: skill.name })}>
    <header><h2>{skill.name}</h2>
      <span className="settings-editor-tags"><Tag minimal round>{t("MCP · {server}", { server: skill.server })}</Tag>
        <Tag minimal round intent={skill.enabled ? "success" : "none"}>{t(skill.enabled ? "Enabled" : "Disabled")}</Tag>
        {skill.namedLikeLocal && <Tag minimal round intent="warning">{t("Named like a local skill")}</Tag>}
        {skill.dynamic && <Tag minimal round>{t("Generated by its server")}</Tag>}
        {readContent && <Button size="small" icon={<AppIcon name="eye" size={15} />} data-mcp-show-content onClick={toggleContent}>{t(content ? "Hide content" : "Show content")}</Button>}</span></header>
    <p className="skill-detail-description">{skill.description}</p>
    <dl className="skill-detail-facts">
      <div><dt>{t("Address")}</dt><dd><code>{skill.uri}</code></dd></div>
      <div><dt>{t("Approval")}</dt><dd><span data-mcp-approval={skill.approval}>{t(mcpApprovalLabels[skill.approval] ?? "Not approved")}</span>
        {onForget && (skill.approval === "approved" || skill.approval === "changed")
          && <ForgetApprovalButton name={skill.name} disabled={disabled} onForget={() => onForget(skill)} />}</dd></div>
      {fields.map(field => <div key={`field:${field.name}`} data-mcp-field={field.name}><dt>{field.name}</dt><dd><code>{field.value}</code>
        {field.ignored && <Tag minimal round>{t("Ignored")}</Tag>}</dd></div>)}
      {!skill.dynamic && <div><dt>{t("Files")}</dt><dd className="skill-mcp-files">
        <span>{t(skill.fileCount === 1 ? "1 file, {size}" : "{count} files, {size}", { count: skill.fileCount, size: formatFileSize(skill.totalBytes, locale) })}</span>
        {skill.files.map(file => <span key={file.path} data-mcp-file={file.path}><code>{file.path}</code> <span className="bp6-text-muted">{formatFileSize(file.size, locale)}</span></span>)}</dd></div>}
    </dl>
    {content === "reading" && <p role="status" className="bp6-text-muted">{t("Reading the skill…")}</p>}
    {read !== undefined && (instructions === null
      ? <p role="alert" className="error-text">{read?.message || t("The skill could not be read.")}</p>
      : <>
        {!read!.verified && <p className="bp6-text-muted" data-mcp-unverified><Tag minimal round intent="warning">{t("Not verified")}</Tag></p>}
        {instructions ? <article className="skill-detail-instructions" aria-label="SKILL.md"><MarkdownContent source={instructions} document /></article>
          : <p className="bp6-text-muted">{t("The skill file has no instructions.")}</p>}
        {read!.truncated && <p className="bp6-text-muted">{t("Only the beginning of the skill file is shown.")}</p>}
      </>)}
  </section>;
}

/**
 * Settings page for skills: one list with an enable switch per skill, the selected skill's details, bulk actions and
 * skill creation. The folder of a skill is opened in the code editor, and so is the one of a skill that was created.
 * The skills of the MCP servers are listed below the local ones, read from their servers.
 */
export function SkillSettings({ epoch, project, onEdit, onOpenFile, api = skills, reveal = projectFiles.reveal, filesApi, spaceScope = null }: {
  epoch: string | null; project: SettingsProject;
  /** The spaces of the skills of the user, which each row shows and changes; null with the default space alone. */
  spaceScope?: ResourceSpaces;
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
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<{ id: string; epoch: string | null; projectId: string | null; listing: typeof listing; value: SkillsDetailResponse | null }>();
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
  const selected = shown.find(skill => skill.id === selectedId) ?? shown[0];
  const id = selected?.id, source = selected?.source;
  useEffect(() => {
    if (!id || !source) return;
    const abort = new AbortController();
    void api.detail({ expectedEpoch: epoch, projectId, id, name: null, source }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(value => { if (!abort.signal.aborted) setDetail({ id, epoch, projectId, listing, value: value.status === "ok" ? value : null }); },
        () => { if (!abort.signal.aborted) setDetail({ id, epoch, projectId, listing, value: null }); });
    return () => abort.abort();
  }, [api, epoch, projectId, id, source, listing]);
  const shownDetail = selected && detail?.id === selected.id && detail.epoch === epoch && detail.projectId === projectId && detail.listing === listing ? detail : undefined;
  // The skills of the MCP servers are read beside the local listing, and again with it: a failure here leaves the local
  // list as it is.
  const [mcp, setMcp] = useState<{ key: string; value: SkillsMcpListResponse | null }>();
  const mcpKey = `${epoch}:${projectId}`;
  useEffect(() => {
    if (!listing) return;
    const abort = new AbortController();
    void api.listMcp({ expectedEpoch: epoch, projectId }, { signal: abort.signal, timeoutMilliseconds: 20000 })
      .then(value => { if (!abort.signal.aborted) setMcp({ key: mcpKey, value }); }, () => { if (!abort.signal.aborted) setMcp({ key: mcpKey, value: null }); });
    return () => abort.abort();
  }, [api, epoch, projectId, listing]);
  const mcpListing = mcp?.key === mcpKey ? mcp.value : undefined;
  const mcpServers = useMemo(() => {
    const text = filter.trim().toLowerCase();
    return (mcpListing?.status === "ok" ? mcpListing.servers : [])
      .map(server => text ? { ...server, skills: server.skills.filter(skill => `${skill.name} ${skill.description} ${skill.server}`.toLowerCase().includes(text)) } : server)
      .filter(server => server.skills.length > 0 || (!text && server.problems.length > 0));
  }, [mcpListing, filter]);
  const [selectedMcpKey, setSelectedMcpKey] = useState<string | null>(null);
  // A skill of a server is shown when it was chosen, or when no local skill is listed.
  const mcpSkills = mcpServers.flatMap(server => server.skills);
  const selectedMcp = (selectedMcpKey === null ? undefined : mcpSkills.find(skill => mcpSkillKey(skill) === selectedMcpKey)) ?? (shown.length ? undefined : mcpSkills[0]);
  const toggleMcp = (skill: SkillsMcpEntry, enabled: boolean) => void mutate(() => api.setMcpEnabled(
    { expectedEpoch: epoch, projectId, scope: writeScope, server: skill.server, uri: skill.uri, enabled }, { timeoutMilliseconds: 30000 }));
  const forgetMcp = (skill: SkillsMcpEntry) => void mutate(() => api.forgetMcpApproval({ expectedEpoch: epoch, projectId, server: skill.server, uri: skill.uri }, { timeoutMilliseconds: 30000 }));
  // Reading the `SKILL.md` may start its server.
  const readMcp = (skill: SkillsMcpEntry, signal: AbortSignal) => api.mcpContent({ expectedEpoch: epoch, projectId, server: skill.server, uri: skill.uri }, { signal, timeoutMilliseconds: 120000 });
  // The switch shows the effective state; a change is written to the chosen scope. Invalid names have no switch
  // action and are left out of bulk writes, without losing their detail or remove actions. The bulk switches name the
  // filter, not the skills: the host switches every skill it matches, those beyond the listing limit included.
  const omitted = listing?.omitted ?? 0;
  const toggleNames = [...new Set(shown.filter(skill => skillToggleable(skill.name)).map(skill => skill.name))];
  const toggle = (skill: SkillsEntry, enabled: boolean) => {
    if (skillToggleable(skill.name)) void mutate(() => api.setEnabled({ expectedEpoch: epoch, projectId, scope: writeScope, name: skill.name, enabled }, { timeoutMilliseconds: 30000 }));
  };
  const toggleAll = (enabled: boolean) => {
    if (toggleNames.length || omitted) void mutate(() => api.setMatchingEnabled({ expectedEpoch: epoch, projectId, scope: writeScope, query: filter.trim(), enabled }, { timeoutMilliseconds: 30000 }));
  };
  const remove = (skill: SkillsEntry) => void mutate(() => api.delete({ expectedEpoch: epoch, projectId, id: skill.id, name: null, source: skill.source }, { timeoutMilliseconds: 120000 }), "Removed.");
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
    notice={notice} loading={loading} busy={busy} onReload={() => { reload(); spaceScope?.refresh?.(); }}
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
        <Button size="small" disabled={busy || (toggleNames.length === 0 && omitted === 0)} onClick={() => toggleAll(true)}>{t("Enable all")}</Button>
        <Button size="small" disabled={busy || (toggleNames.length === 0 && omitted === 0)} onClick={() => toggleAll(false)}>{t("Disable all")}</Button>
      </div>
      {omitted > 0 && <p className="bp6-text-muted skill-settings-omitted" role="status">
        {t("{count} more skills are not listed here. Enable all and Disable all act on them too when they match the filter.", { count: omitted })}</p>}
      <div className="settings-editor-layout skill-settings-layout">
        <div className="skill-settings-lists">
          <SkillRows spaceScope={spaceScope} skills={shown} empty={all.length ? "No skill matches the filter." : "No skills were found."} selected={selectedMcp ? undefined : selected} disabled={busy}
            onSelect={skill => { setSelectedId(skill.id); setSelectedMcpKey(null); }} onToggle={toggle} onEdit={onEdit} onDelete={remove} />
          <McpSkillRows servers={mcpServers} selected={selectedMcp ? mcpSkillKey(selectedMcp) : null} disabled={busy} onSelect={skill => setSelectedMcpKey(mcpSkillKey(skill))} onToggle={toggleMcp} />
          {mcpListing !== undefined && (mcpListing === null || !["ok", "unavailable"].includes(mcpListing.status))
            && <p className="bp6-text-muted skill-settings-mcp-failed" role="status">{t("The skills of the MCP servers could not be read.")}</p>}
        </div>
        {selectedMcp ? <McpSkillDetail key={`${epoch}:${projectId}:${mcpSkillKey(selectedMcp)}`} skill={selectedMcp} disabled={busy} onForget={forgetMcp} readContent={readMcp} />
          : selected ? <SkillDetail key={`${epoch}:${projectId}:${selected.id}`} spaceScope={spaceScope} skill={selected} detail={shownDetail?.value ?? undefined} failed={shownDetail?.value === null} onEdit={onEdit} onDelete={remove} disabled={busy} platform={files.platform} onReveal={revealSkill} />
          : <NonIdealState icon={<AppIcon name="skill" size={32} />} title={t("No skills were found.")} />}
      </div>
    </>}
  </SettingsPage>;
}
