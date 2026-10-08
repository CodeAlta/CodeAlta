import { useEffect, useRef, useState } from "react";
import { Button, Card, CardList, Checkbox, FormGroup, HTMLSelect, InputGroup, Menu, MenuItem, PopoverNext, Section, SectionCard, Tag } from "@blueprintjs/core";
import { agentPrompts, type AgentPromptDocument, type AgentPromptEntry } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { CopilotTag, copilotPromptScope } from "./CopilotTag";
import { PromptRows, promptIdentity as identity } from "./PromptRows";
import { CodeEditor } from "./monaco/CodeEditor";
import { SettingsFileLocation, SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
import { settingsFailure, type SettingsScope } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

type Kind = "Agent" | "System";
type PromptForm = Readonly<{ id: string; kind: Kind; scope: SettingsScope; name: string; description: string; body: string;
  systemPromptId: string; append: boolean; revision: string | null }>;
const added = "\u0000new";
const formOf = (document: AgentPromptDocument): PromptForm => ({ id: document.id, kind: document.kind === "System" ? "System" : "Agent",
  scope: document.scope === "Project" ? "Project" : "Global", name: document.name ?? "", description: document.description ?? "", body: document.body,
  systemPromptId: document.systemPromptId ?? "", append: document.append, revision: document.revision });

/** Settings page for agent and system prompts: the prompt files on the left, an editor on the right. */
export function AgentPromptSettings({ epoch, project, onOpenFile, api = agentPrompts, filesApi }: {
  epoch: string | null; project: SettingsProject;
  /** Called once the code editor was asked to show a prompt file or a folder of prompts: the window leaves Settings. */
  onOpenFile?: () => void;
  api?: typeof agentPrompts;
  /** Says where the files of the page are and opens them; the host by default. */
  filesApi?: SettingsFilesApi;
}) {
  const { t } = useShellLanguage();
  const projectId = project?.id ?? null;
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(
    options => api.list({ expectedEpoch: epoch, projectId }, options), `${epoch}:${projectId}`);
  const [selected, setSelected] = useState<string | null>(null);
  const [document, setDocument] = useState<AgentPromptDocument | null>(null);
  const [form, setForm] = useState<PromptForm | null>(null);
  const [reading, setReading] = useState(false);
  const request = useRef<AbortController | null>(null);
  const prompts = listing?.prompts ?? [];
  const entry = selected && selected !== added ? prompts.find(prompt => identity(prompt) === selected) ?? null : null;
  const entryKey = entry ? identity(entry) : null;
  const files = useSettingsFiles({ page: "prompts", epoch, projectId, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });

  // Keep the selection across reloads; otherwise open the first prompt.
  useEffect(() => {
    if (!listing || selected === added) return;
    if (!listing.prompts.some(prompt => identity(prompt) === selected)) setSelected(listing.prompts[0] ? identity(listing.prompts[0]) : null);
  }, [listing]);
  // Read the selected prompt's text (again after every reload, which follows a save).
  useEffect(() => {
    request.current?.abort();
    if (!entry) { if (selected !== added) { setDocument(null); setForm(null); } return; }
    const controller = new AbortController(); request.current = controller;
    setReading(true);
    api.read({ expectedEpoch: epoch, projectId, id: entry.id, kind: entry.kind, scope: entry.scope }, { signal: controller.signal, timeoutMilliseconds: 20000 }).then(value => {
      if (controller.signal.aborted) return;
      const failure = settingsFailure(value.status, value.message);
      if (failure || !value.prompt) { setDocument(null); setForm(null); setNotice(failure ?? settingsFailure("read_failed")); return; }
      setDocument(value.prompt); setForm(formOf(value.prompt));
    }).catch(() => { if (!controller.signal.aborted) { setDocument(null); setForm(null); setNotice(settingsFailure("read_failed")); } })
      .finally(() => { if (!controller.signal.aborted) setReading(false); });
    return () => controller.abort();
  }, [entryKey, listing]);

  const readOnly = !!document?.readOnly && selected !== added;
  const baseline = document && selected !== added ? formOf(document) : null;
  const dirty = !!form && (selected === added ? form.id !== "" || form.body !== "" : !!baseline && JSON.stringify(form) !== JSON.stringify(baseline));
  const problem: MessageKey | null = !form ? null
    : !/^[A-Za-z0-9._-]{1,128}$/.test(form.id.trim()) ? "Use 1 to 128 letters, digits, dots, dashes or underscores for the name."
    : selected === added && prompts.some(prompt => prompt.id === form.id.trim() && prompt.kind === form.kind && prompt.scope === form.scope) ? "A prompt with this name already exists in that scope."
    : !form.body.trim() ? "Enter the prompt text." : null;
  const edit = (change: Partial<PromptForm>) => setForm(current => current ? { ...current, ...change } : current);
  const systemPrompts = [...new Set(prompts.filter(prompt => prompt.kind === "System").map(prompt => prompt.id))];
  function create(kind: Kind, from?: PromptForm) {
    if (busy) return;
    setNotice(null); setSelected(added); setDocument(null);
    setForm({ id: from?.id ?? "", kind, scope: "Global", name: from?.name ?? "", description: from?.description ?? "", body: from?.body ?? "",
      systemPromptId: from?.systemPromptId ?? "", append: from?.append ?? kind === "Agent", revision: null });
  }
  async function save() {
    if (!form || problem || busy || readOnly) return;
    const id = form.id.trim();
    // A system prompt keeps whatever name and description its file already had.
    const agent = form.kind === "Agent";
    if (await mutate(() => api.save({ expectedEpoch: epoch, projectId, id, kind: form.kind, scope: form.scope, expectedRevision: form.revision,
      name: agent ? form.name.trim() || id : document?.name ?? null, description: agent ? form.description.trim() || null : document?.description ?? null,
      body: form.body, systemPromptId: agent && form.systemPromptId ? form.systemPromptId : null, append: agent && form.append }, { timeoutMilliseconds: 30000 }), "Saved."))
      setSelected(identity({ kind: form.kind, scope: form.scope, id }));
  }
  async function remove() {
    if (!document || readOnly || busy) return;
    if (await mutate(() => api.delete({ expectedEpoch: epoch, projectId, id: document.id, kind: document.kind, scope: document.scope, expectedRevision: document.revision },
      { timeoutMilliseconds: 30000 }), "Removed.")) setSelected(null);
  }
  const group = (kind: Kind, title: MessageKey) => <PromptRows prompts={prompts.filter(prompt => prompt.kind === kind)} title={title} selected={selected} disabled={busy}
    onSelect={prompt => { if (!busy) { setNotice(null); setSelected(identity(prompt)); } }}
    onOpen={files.open ? prompt => files.open!({ kind: "prompt", scope: prompt.scope, id: prompt.id, part: prompt.kind }) : undefined} />;

  return <SettingsPage className="prompt-settings" label={t("Agent prompts")} group="Agent & models" title="Agent prompts" description="Instructions that define how an agent behaves. Pick one per session from the prompt bar."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<PopoverNext placement="bottom-end" content={<Menu>
      <MenuItem icon={<AppIcon name="assistant" size={15} />} text={t("Agent prompt")} onClick={() => create("Agent")} />
      <MenuItem icon={<AppIcon name="prompt" size={15} />} text={t("System prompt")} onClick={() => create("System")} /></Menu>}>
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!listing || busy} endIcon={<AppIcon name="chevronDown" size={14} />}>{t("New prompt")}</Button></PopoverNext>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="assistant" title="Agent prompts unavailable" />
      : <><SettingsFileLocations files={files} disabled={busy} /><div className="settings-editor-layout">
        <CardList compact className="settings-editor-list" aria-label={t("Agent prompts")}>
          {group("Agent", "Agent prompts")}{group("System", "System prompts")}
          {selected === added && <Card interactive selected><span className="settings-editor-name"><strong>{form?.id || t("New prompt")}</strong></span>
            <Tag minimal round intent="warning">{t("Unsaved changes")}</Tag></Card>}
        </CardList>
        {form ? <Section className="settings-editor-form prompt-settings-form" title={selected === added ? t("New prompt") : entry?.name || form.id}
          subtitle={`${t(form.kind === "System" ? "System prompt" : "Agent prompt")} · ${form.id || "…"}`}
          rightElement={reading ? <ActivitySpinner size={14} /> : readOnly ? copilotPromptScope(document?.scope ?? "") ? <CopilotTag /> : <Tag minimal round>{t("Built-in")}</Tag> : undefined}>
          {document?.file && selected !== added && <SectionCard className="settings-editor-file">
            <SettingsFileLocation path={document.file} readOnly={readOnly && !copilotPromptScope(document.scope)} platform={files.platform} disabled={busy}
              onOpen={files.open ? () => files.open!({ kind: "prompt", scope: document.scope, id: document.id, part: document.kind }) : undefined}
              onReveal={() => files.reveal({ kind: "prompt", scope: document.scope, id: document.id, part: document.kind })} /></SectionCard>}
          <SectionCard className="settings-editor-fields">
            {selected === added && <FormGroup label={t("Name")} labelFor="prompt-id">
              <InputGroup id="prompt-id" value={form.id} disabled={busy} maxLength={128} spellCheck={false} placeholder="my-agent" onChange={event => edit({ id: event.target.value })} /></FormGroup>}
            {selected === added && project && <FormGroup label={t("Stored in")}><ScopeChoice value={form.scope} project={project} disabled={busy} onChange={scope => edit({ scope })} /></FormGroup>}
            {form.kind === "Agent" && <>
              <FormGroup label={t("Display name")} labelFor="prompt-name">
                <InputGroup id="prompt-name" value={form.name} disabled={busy || readOnly} placeholder={form.id} onChange={event => edit({ name: event.target.value })} /></FormGroup>
              <FormGroup label={t("System prompt")} labelFor="prompt-system">
                <HTMLSelect id="prompt-system" fill value={form.systemPromptId} disabled={busy || readOnly} onChange={event => edit({ systemPromptId: event.target.value })}>
                  <option value="">{t("Default")}</option>
                  {[...new Set([...(form.systemPromptId ? [form.systemPromptId] : []), ...systemPrompts])].map(id => <option key={id} value={id}>{id}</option>)}
                </HTMLSelect></FormGroup>
              <FormGroup label={t("Description")} labelFor="prompt-description" className="settings-editor-wide">
                <InputGroup id="prompt-description" value={form.description} disabled={busy || readOnly} onChange={event => edit({ description: event.target.value })} /></FormGroup>
              <Checkbox className="settings-editor-wide" checked={form.append} disabled={busy || readOnly} label={t("Add to the system prompt instead of replacing it")}
                onChange={event => edit({ append: event.currentTarget.checked })} />
            </>}
          </SectionCard>
          <SectionCard className="prompt-settings-editor"><CodeEditor language="markdown" wrap label={t("Prompt text")} value={form.body} readOnly={busy || readOnly} onChange={body => edit({ body })} /></SectionCard>
          <SectionCard className="settings-editor-footer">
            {problem && dirty && !readOnly && <span className="settings-editor-problem" role="alert">{t(problem)}</span>}
            {readOnly ? <Button intent="primary" icon={<AppIcon name="copy" size={15} />} disabled={busy} onClick={() => create(form.kind, form)}>{t("Customize a copy")}</Button> : <>
              <Button intent="primary" disabled={!dirty || !!problem || busy} onClick={() => void save()}>{t("Save")}</Button>
              <Button disabled={selected !== added && !dirty || busy} onClick={() => { if (selected === added) { setForm(null); setSelected(prompts[0] ? identity(prompts[0]) : null); } else if (baseline) setForm(baseline); }}>{t(selected === added ? "Cancel" : "Revert")}</Button>
            </>}
            <span className="settings-editor-spacer" />
            {document && !readOnly && selected !== added && <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Remove {name}?", { name: entry?.name || document.id })}</p>
              <Button intent="danger" disabled={busy} onClick={() => void remove()}>{t("Remove")}</Button></div>}>
              <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} text={t("Remove")} />
            </PopoverNext>}
          </SectionCard>
        </Section> : reading && <SettingsUnavailable loading icon="assistant" title="Agent prompts unavailable" />}
      </div></>}
  </SettingsPage>;
}
