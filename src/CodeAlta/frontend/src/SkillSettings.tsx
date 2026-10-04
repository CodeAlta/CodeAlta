import { useEffect, useMemo, useState } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, NonIdealState, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { skills, type SkillsDetailResponse, type SkillsEntry } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { MarkdownContent } from "./MarkdownContent";
import { skillInstructions } from "./skillDetail";
import { ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
import { settingsFailure, type SettingsScope } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const sourceLabels: Record<string, MessageKey> = { ProjectAlta: "Project", ProjectCommon: "Project", UserAlta: "User", UserCommon: "User", Plugin: "Plugin", Builtin: "Built-in" };

const skillKey = (skill: Pick<SkillsEntry, "name" | "source">) => `${skill.source}:${skill.name}`;

// What the selected skill is, where it lives and what its SKILL.md tells the agent.
function SkillDetail({ skill, detail, failed }: { skill: SkillsEntry; detail: SkillsDetailResponse | undefined; failed: boolean }) {
  const { t } = useShellLanguage();
  const facts: [MessageKey, string | null | undefined][] = !detail ? [] : [
    ["Skill file", detail.skillFilePath], ["Overridden by", detail.shadowedBy], ["License", detail.license],
    ["Compatibility", detail.compatibility], ["Allowed tools", detail.allowedTools]];
  const instructions = detail?.content ? skillInstructions(detail.content) : "";
  return <section className="skill-detail" aria-label={t("Details for {name}", { name: skill.title || skill.name })}>
    <header><h2>{skill.title || skill.name}</h2>
      <span className="settings-editor-tags"><Tag minimal round>{t(sourceLabels[skill.source] ?? "Plugin")}</Tag>
        <Tag minimal round intent={skill.enabled ? "success" : "none"}>{t(skill.enabled ? "Enabled" : "Disabled")}</Tag>
        {detail && !detail.modelVisible && skill.enabled && <Tag minimal round intent="warning">{t("Not offered to the model")}</Tag>}
        {skill.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
        {!skill.valid && <Tag minimal round intent="danger">{t("Invalid")}</Tag>}</span></header>
    <p className="skill-detail-description">{skill.description}</p>
    {failed && <p role="alert" className="error-text">{t("The skill details could not be read.")}</p>}
    {detail && <>
      <dl className="skill-detail-facts">
        {facts.filter(([, value]) => !!value).map(([label, value]) => <div key={label}><dt>{t(label)}</dt><dd><code>{value}</code></dd></div>)}
        {detail.relatedFiles.length > 0 && <div><dt>{t("Related files")}</dt><dd>{detail.relatedFiles.map(file => <code key={file.path}>{file.path}</code>)}
          {detail.relatedFilesOmitted > 0 && <span className="bp6-text-muted">+{detail.relatedFilesOmitted}</span>}</dd></div>}
      </dl>
      {detail.diagnostics.length > 0 && <ul className="skill-detail-diagnostics" aria-label={t("Diagnostics")}>
        {detail.diagnostics.map((item, index) => <li key={index} data-severity={item.severity.toLowerCase()}><strong>{item.code}</strong> {item.message}</li>)}</ul>}
      {instructions ? <article className="skill-detail-instructions" aria-label="SKILL.md"><MarkdownContent source={instructions} /></article>
        : <p className="bp6-text-muted">{t(detail.contentTruncated ? "The skill file is too large to show." : "The skill file has no instructions.")}</p>}
      {instructions && detail.contentTruncated && <p className="bp6-text-muted">{t("Only the beginning of the skill file is shown.")}</p>}
    </>}
  </section>;
}

/** Settings page for skills: one list with an enable switch per skill, the selected skill's details, bulk actions and skill creation. */
export function SkillSettings({ epoch, project, api = skills }: { epoch: string | null; project: SettingsProject; api?: typeof skills }) {
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
        <CardList compact className="settings-editor-rows settings-editor-list" aria-label={t("Skills")}>
          {shown.map(skill => <Card key={skillKey(skill)} interactive selected={skill === selected} aria-current={skill === selected ? "true" : undefined}
            onClick={event => { if (!(event.target as HTMLElement).closest(".bp6-switch")) setSelectedKey(skillKey(skill)); }}>
            <span className="settings-editor-name"><strong>{skill.title || skill.name}</strong><small>{skill.description}</small></span>
            <span className="settings-editor-tags"><Tag minimal round>{t(sourceLabels[skill.source] ?? "Plugin")}</Tag>
              {skill.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
              {!skill.valid && <Tag minimal round intent="danger">{t("Invalid")}</Tag>}
              <Switch checked={skill.enabled} disabled={busy} aria-label={t("Enable {name}", { name: skill.name })} onChange={event => toggle(skill, event.currentTarget.checked)} /></span>
          </Card>)}
          {shown.length === 0 && <Card><span className="bp6-text-muted">{t(all.length ? "No skill matches the filter." : "No skills were found.")}</span></Card>}
        </CardList>
        {selected ? <SkillDetail skill={selected} detail={shownDetail?.value ?? undefined} failed={shownDetail?.value === null} />
          : <NonIdealState icon={<AppIcon name="skill" size={32} />} title={t("No skills were found.")} />}
      </div>
    </>}
  </SettingsPage>;
}
