import { useEffect, useRef, useState } from "react";
import { TriggerIcon } from "./TriggerIcon";
import { Button, ButtonGroup, Callout, FormGroup, HTMLSelect, InputGroup, Menu, MenuItem, NumericInput, PopoverNext, SegmentedControl, Switch, TextArea } from "@blueprintjs/core";
import { modelCatalog, workspace, type AutomationTriggerItem, type ModelCatalogModel, type SessionPromptChoice, type WorkspaceProject } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { dayName, formProblem, inputOf, isSchedule as schedule, maximumCommandLength, maximumFolderLength, maximumNameLength, maximumPromptLength, maximumTriggers, needsProject,
  newTrigger, triggerLabel, triggerTone, triggerTypes, weekDays, type AutomationForm, type TriggerType } from "./automations";
import type { AutomationsHub } from "./automationsHub";

const hours = [1, 2, 3, 4, 6, 8, 12];

/** The next times a schedule being written is due, asked of the host a moment after the last change. */
function TriggerPreview({ hub, trigger }: { hub: AutomationsHub; trigger: AutomationTriggerItem }) {
  const { t, locale } = useShellLanguage();
  const [preview, setPreview] = useState<Readonly<{ times: readonly string[]; problem: string | null }> | null>(null);
  const key = JSON.stringify(trigger);
  useEffect(() => {
    const abort = new AbortController();
    const timer = setTimeout(() => { void hub.preview(trigger, abort.signal).then(value => { if (!abort.signal.aborted) setPreview(value); }); }, 250);
    return () => { clearTimeout(timer); abort.abort(); };
  }, [hub, key]);
  if (!preview) return null;
  if (preview.problem) return <p className="automation-trigger-preview" data-problem role="alert">{preview.problem}</p>;
  if (preview.times.length === 0) return null;
  const format = (value: string) => {
    try { return new Intl.DateTimeFormat(locale, { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }).format(new Date(value)); }
    catch { return value; }
  };
  return <p className="automation-trigger-preview"><span>{t("Next")}</span>{preview.times.slice(0, 3).map(time => <time key={time} dateTime={time}>{format(time)}</time>)}</p>;
}

function Times({ value, disabled, onChange }: { value: readonly string[]; disabled: boolean; onChange: (value: readonly string[]) => void }) {
  const { t } = useShellLanguage();
  return <span className="automation-times">
    {value.map((time, index) => <span key={index} className="automation-time">
      <input type="time" value={time} disabled={disabled} aria-label={t("Time")} onChange={event => { if (event.target.value) onChange(value.map((other, at) => at === index ? event.target.value : other)); }} />
      {value.length > 1 && <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="close" size={12} />} aria-label={t("Remove this time")} title={t("Remove this time")}
        onClick={() => onChange(value.filter((_, at) => at !== index))} />}
    </span>)}
    {value.length < 6 && <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="plus" size={13} />} aria-label={t("Add a time")} title={t("Add a time")}
      onClick={() => onChange([...value, value.includes("12:00") ? "18:00" : "12:00"])} />}
  </span>;
}

export function TriggerRow({ hub, trigger, chat, disabled, onChange, onRemove }: {
  hub: AutomationsHub; trigger: AutomationTriggerItem;
  /** Whether the automation runs in no project: its command then runs in the home folder. */
  chat: boolean;
  disabled: boolean; onChange: (value: AutomationTriggerItem) => void; onRemove: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const set = (change: Partial<AutomationTriggerItem>) => onChange({ ...trigger, ...change });
  return <li className="automation-trigger">
    <span className="automation-trigger-icon" data-file-tone={triggerTone(trigger.type)}><TriggerIcon type={trigger.type} size={15} /></span>
    <div className="automation-trigger-fields">
      <HTMLSelect value={trigger.type} disabled={disabled} aria-label={t("Trigger")} onChange={event => onChange(newTrigger(event.target.value as TriggerType))}>
        {triggerTypes.map(type => <option key={type} value={type}>{t(triggerLabel(type))}</option>)}
      </HTMLSelect>
      {trigger.type === "hourly" && <>
        <span>{t("every")}</span>
        <HTMLSelect value={trigger.every} disabled={disabled} aria-label={t("Hours between runs")} onChange={event => set({ every: Number(event.target.value) })}>
          {hours.map(value => <option key={value} value={value}>{t(value === 1 ? "{count} hour" : "{count} hours", { count: value })}</option>)}
        </HTMLSelect>
        <span>{t("at minute")}</span>
        <NumericInput className="automation-minute" value={trigger.minute} min={0} max={59} disabled={disabled} buttonPosition="none" aria-label={t("Minute")}
          onValueChange={value => { if (Number.isInteger(value) && value >= 0 && value <= 59) set({ minute: value }); }} />
      </>}
      {trigger.type === "weekly" && <ButtonGroup className="automation-days" aria-label={t("Days")}>
        {weekDays.map(day => <Button key={day} size="small" disabled={disabled} active={trigger.days.includes(day)} aria-pressed={trigger.days.includes(day)}
          onClick={() => set({ days: trigger.days.includes(day) ? trigger.days.filter(other => other !== day) : [...trigger.days, day] })}>{dayName(day, locale)}</Button>)}
      </ButtonGroup>}
      {(trigger.type === "daily" || trigger.type === "weekly") && <><span>{t("at")}</span><Times value={trigger.at} disabled={disabled} onChange={at => set({ at: [...at] })} /></>}
      {trigger.type === "cron" && <InputGroup className="automation-cron" value={trigger.expression ?? ""} disabled={disabled} spellCheck={false} maxLength={128}
        aria-label={t("Cron expression")} placeholder="0 9 * * 1-5" onChange={event => set({ expression: event.target.value })} />}
      {trigger.type === "pull_request" && <HTMLSelect value={trigger.event} disabled={disabled} aria-label={t("Event")} onChange={event => set({ event: event.target.value })}>
        <option value="opened">{t("is opened")}</option><option value="updated">{t("receives commits")}</option>
      </HTMLSelect>}
      {trigger.type === "issue" && <span>{t("is opened")}</span>}
      {trigger.type === "jira" && <HTMLSelect value={trigger.event === "updated" ? "updated" : "created"} disabled={disabled} aria-label={t("Event")} onChange={event => set({ event: event.target.value })}>
        <option value="created">{t("is created")}</option><option value="updated">{t("is updated")}</option>
      </HTMLSelect>}
      {trigger.type === "command" && <>
        <InputGroup className="automation-command" value={trigger.command ?? ""} disabled={disabled} spellCheck={false} maxLength={maximumCommandLength}
          aria-label={t("Command")} placeholder="gh run watch 123 --exit-status" onChange={event => set({ command: event.target.value })} />
        <span>{t("in")}</span>
        <InputGroup className="automation-command-folder" value={trigger.folder ?? ""} disabled={disabled} spellCheck={false} maxLength={maximumFolderLength}
          aria-label={t("Working folder")} title={t("Working folder")} placeholder={t(chat ? "Home folder" : "Project folder")}
          onChange={event => set({ folder: event.target.value })} />
      </>}
      {(trigger.type === "issue" || trigger.type === "pull_request") && <HTMLSelect value={trigger.authors} disabled={disabled} aria-label={t("Authors")} onChange={event => set({ authors: event.target.value })}>
        <option value="trusted">{t("by a member")}</option><option value="anyone">{t("by anyone")}</option>
      </HTMLSelect>}
      {schedule(trigger) && <TriggerPreview hub={hub} trigger={trigger} />}
    </div>
    <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove this trigger")} title={t("Remove this trigger")} onClick={onRemove} />
  </li>;
}

/**
 * The window that writes an automation: its name, where it runs, what starts it, the prompt it sends and the
 * model it asks for. Saving writes its configuration file; nothing runs before that.
 */
export function AutomationEditor({ hub, initial, projects, providers, epoch, onClose, onSaved }: {
  hub: AutomationsHub; initial: AutomationForm;
  /** The projects an automation can run in. */
  projects: readonly WorkspaceProject[];
  /** The enabled providers, the default one first. */
  providers: readonly Readonly<{ id: string }>[];
  epoch: string | null | undefined;
  onClose: () => void;
  /** Called once the automation is written, with whether it is to run now. */
  onSaved: (id: string, run: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [form, setForm] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [models, setModels] = useState<readonly ModelCatalogModel[]>([]);
  const [prompts, setPrompts] = useState<readonly SessionPromptChoice[]>([]);
  const name = useRef<HTMLInputElement>(null);
  const edit = (change: Partial<AutomationForm>) => { setForm(current => ({ ...current, ...change })); setProblem(null); };
  const project = projects.find(candidate => candidate.id === form.projectId);

  useEffect(() => {
    setModels([]);
    if (!epoch || !form.provider) return;
    const abort = new AbortController();
    void modelCatalog.models({ expectedEpoch: epoch, providerId: form.provider }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(reply => { if (!abort.signal.aborted && reply.status === "ok") setModels(reply.models); }).catch(() => { /* The model stays a free choice of the host. */ });
    return () => abort.abort();
  }, [epoch, form.provider]);
  useEffect(() => {
    setPrompts([]);
    if (!epoch) return;
    const abort = new AbortController();
    void workspace.draftPrompts({ expectedHostEpoch: epoch, projectId: project?.id ?? null, projectPath: project?.path ?? null }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(reply => { if (!abort.signal.aborted && reply.status === "ok") setPrompts(reply.prompts); }).catch(() => { /* Only the default prompt is offered. */ });
    return () => abort.abort();
  }, [epoch, project?.id, project?.path]);

  const efforts = models.find(model => model.id === form.model)?.efforts ?? [];
  async function save(run: boolean) {
    const missing = formProblem(form);
    if (missing) { setProblem(t(missing)); return; }
    setBusy(true);
    const outcome = await hub.save(inputOf(form), form.projectId && form.storeInProject ? form.projectId : null);
    setBusy(false);
    if (outcome.ok && outcome.id) onSaved(outcome.id, run);
    else setProblem(outcome.message ?? t("The automation could not be saved."));
  }
  // An event is one of the repository of a project: a chat has none.
  const add = <Menu>{triggerTypes.map(type => {
    const missing = needsProject({ type }) && !form.projectId;
    return <MenuItem key={type} icon={<span data-file-tone={triggerTone(type)}><TriggerIcon type={type} size={15} /></span>} text={t(triggerLabel(type))}
      disabled={missing} label={missing ? t("Needs a project") : undefined} onClick={() => edit({ triggers: [...form.triggers, newTrigger(type)] })} />;
  })}</Menu>;

  return <AppWindow storageKey="codealta.desktop.window.automation.v1" className="automation-editor-dialog" titleId="automation-editor-title"
    title={t(form.id ? "Edit automation" : "New automation")} preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(820, viewport.height - 40) })}
    minimumSize={{ width: 520, height: 420 }} onClose={onClose} closeLabel={t("Close")} onOpened={() => name.current?.focus()} keepOnOutsidePress
    onCancel={event => { event.preventDefault(); if (!busy) onClose(); }} onKeyDown={event => event.stopPropagation()}>
    <form className="automation-editor" onSubmit={event => { event.preventDefault(); void save(false); }}>
      <div className="automation-editor-body">
        <FormGroup label={t("Name")} labelFor="automation-name">
          <InputGroup id="automation-name" inputRef={name} value={form.name} disabled={busy} maxLength={maximumNameLength} placeholder={t("Nightly review")}
            onChange={event => edit({ name: event.target.value })} />
        </FormGroup>
        <div className="automation-editor-row">
          <FormGroup label={t("Runs in")} labelFor="automation-project">
            <HTMLSelect id="automation-project" fill value={form.projectId ?? ""} disabled={busy}
              onChange={event => edit({ projectId: event.target.value || null, storeInProject: event.target.value ? form.storeInProject : false, agent: "" })}>
              <option value="">{t("A chat, in no project")}</option>
              {form.projectId && !project && <option value={form.projectId}>{t("Unavailable project")}</option>}
              {projects.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name}</option>)}
            </HTMLSelect>
          </FormGroup>
          {project && <FormGroup label={t("Stored in")}>
            <SegmentedControl size="small" value={form.storeInProject ? "project" : "user"} disabled={busy} onValueChange={value => edit({ storeInProject: value === "project" })}
              options={[{ value: "user", label: t("My configuration") }, { value: "project", label: t("The project") }]} />
          </FormGroup>}
        </div>
        <FormGroup label={t("Starts")}>
          <ul className="automation-triggers">
            {form.triggers.length === 0 && <li className="automation-trigger">
              <span className="automation-trigger-icon" data-file-tone="muted"><AppIcon name="hand" size={15} /></span>
              <div className="automation-trigger-fields"><span>{t("When you run it")}</span></div></li>}
            {form.triggers.map((trigger, index) => <TriggerRow key={index} hub={hub} trigger={trigger} chat={!form.projectId} disabled={busy}
              onChange={value => edit({ triggers: form.triggers.map((other, at) => at === index ? value : other) })}
              onRemove={() => edit({ triggers: form.triggers.filter((_, at) => at !== index) })} />)}
          </ul>
          {form.triggers.length < maximumTriggers && <PopoverNext content={add} placement="bottom-start">
            <Button size="small" variant="minimal" disabled={busy} icon={<AppIcon name="plus" size={14} />} endIcon={<AppIcon name="chevronDown" size={13} />}>{t("Add a trigger")}</Button>
          </PopoverNext>}
        </FormGroup>
        <FormGroup label={t("Prompt")} labelFor="automation-prompt" className="automation-prompt-group">
          <TextArea id="automation-prompt" fill value={form.prompt} disabled={busy} maxLength={maximumPromptLength} spellCheck
            placeholder={t("What the session does each time, written for a session that starts with nothing else.")} onChange={event => edit({ prompt: event.target.value })} />
        </FormGroup>
        <div className="automation-editor-row" data-columns="4">
          <FormGroup label={t("Provider")} labelFor="automation-provider">
            <HTMLSelect id="automation-provider" fill value={form.provider} disabled={busy} onChange={event => edit({ provider: event.target.value, model: "", effort: "" })}>
              <option value="">{t("Default provider")}</option>
              {form.provider && !providers.some(provider => provider.id === form.provider) && <option value={form.provider}>{form.provider}</option>}
              {providers.map(provider => <option key={provider.id} value={provider.id}>{provider.id}</option>)}
            </HTMLSelect>
          </FormGroup>
          <FormGroup label={t("Model")} labelFor="automation-model">
            <HTMLSelect id="automation-model" fill value={form.model} disabled={busy || !form.provider} onChange={event => edit({ model: event.target.value, effort: "" })}>
              <option value="">{t("First model listed")}</option>
              {form.model && !models.some(model => model.id === form.model) && <option value={form.model}>{form.model}</option>}
              {models.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}
            </HTMLSelect>
          </FormGroup>
          <FormGroup label={t("Reasoning")} labelFor="automation-effort">
            <HTMLSelect id="automation-effort" fill value={form.effort} disabled={busy || !form.model} onChange={event => edit({ effort: event.target.value })}>
              <option value="">{t("High when supported")}</option>
              {form.effort && !efforts.includes(form.effort) && <option value={form.effort}>{form.effort}</option>}
              {efforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
            </HTMLSelect>
          </FormGroup>
          <FormGroup label={t("Agent prompt")} labelFor="automation-agent">
            <HTMLSelect id="automation-agent" fill value={form.agent || "default"} disabled={busy} onChange={event => edit({ agent: event.target.value === "default" ? "" : event.target.value })}>
              {!prompts.some(prompt => prompt.id === "default") && <option value="default">{t("Default")}</option>}
              {form.agent && !prompts.some(prompt => prompt.id === form.agent) && <option value={form.agent}>{form.agent}</option>}
              {prompts.map(prompt => <option key={prompt.id} value={prompt.id}>{prompt.name}</option>)}
            </HTMLSelect>
          </FormGroup>
        </div>
        <div className="automation-editor-switches">
          <Switch checked={form.enabled} disabled={busy} label={t("Enabled")} onChange={event => edit({ enabled: event.currentTarget.checked })} />
          {form.triggers.length > 0 && <Switch checked={form.catchUp} disabled={busy} label={t("Catch up on what was missed while CodeAlta was closed")}
            onChange={event => edit({ catchUp: event.currentTarget.checked })} />}
        </div>
        {problem && <Callout intent="danger" compact role="alert">{problem}</Callout>}
      </div>
      <footer className="automation-editor-footer">
        <Button disabled={busy} onClick={onClose}>{t("Cancel")}</Button>
        <Button disabled={busy} icon={<AppIcon name="play" size={14} />} onClick={() => void save(true)}>{t("Save and run")}</Button>
        <Button type="submit" intent="primary" loading={busy}>{t("Save")}</Button>
      </footer>
    </form>
  </AppWindow>;
}
