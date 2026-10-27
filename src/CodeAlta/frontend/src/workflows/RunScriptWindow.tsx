import { useMemo, useRef, useState } from "react";
import { Button, Callout, Checkbox, FormGroup, HTMLSelect, InputGroup, TextArea } from "@blueprintjs/core";
import type { WorkflowScriptItem, WorkspaceProject } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { SessionSettingsChip, useSessionSettingsChoices } from "../sessionSettings/SessionSettingsChip";
import { defaultSessionSettings, startRequest } from "../sessionSettings/sessionSettings";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import type { RunProvider } from "../workItems/runsWith";
import { argumentsOf, fieldsOf, initialValues, type FieldValue } from "./workflows";
import type { WorkflowsHub } from "./workflowsHub";

const noProviders: readonly RunProvider[] = [];

/**
 * Run… on a script of the library: a field for each of its parameters, the project it runs in, what its session runs
 * with (the Agent chip, as for an automation or an issue), and Run, which starts it in a new session of that project.
 * The model of that session runs the script, and is the one of the sessions the script starts without a tier. The
 * session opens once it exists.
 */
export function RunScriptWindow({ hub, script, projects, projectId, providers = noProviders, sessionSettings = null, onClose, onStarted }: {
  hub: WorkflowsHub; script: WorkflowScriptItem;
  /** The projects a script can run in. */
  projects: readonly WorkspaceProject[];
  /** The project the library was read for, where the script runs unless the user says otherwise. */
  projectId: string | null;
  /** The providers the session can use; none hides the Agent chip, and the session takes the defaults. */
  providers?: readonly RunProvider[];
  /** Reads what the form of the Agent chip offers for a project; null where nothing reads it. */
  sessionSettings?: ((projectId: string) => SessionSettingsApi | null) | null;
  onClose: () => void;
  onStarted: (sessionId: string) => void;
}) {
  const { t } = useShellLanguage();
  const fields = useMemo(() => fieldsOf(script.parameters), [script.parameters]);
  const [values, setValues] = useState(() => initialValues(script.parameters));
  const [project, setProject] = useState(projectId ?? projects[0]?.id ?? "");
  const [settings, setSettings] = useState(defaultSessionSettings);
  const settingsApi = useMemo(() => project && sessionSettings ? sessionSettings(project) : null, [project, sessionSettings]);
  const choices = useSessionSettingsChoices(settingsApi, providers, null, settings);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const first = useRef<HTMLElement | null>(null);
  const edit = (name: string, value: FieldValue) => { setValues(current => ({ ...current, [name]: value })); setProblem(null); };

  async function run() {
    if (!project) { setProblem(t("Choose a project.")); return; }
    const args = argumentsOf(fields, values);
    if ("problem" in args) { setProblem(t(args.problem.message, args.problem.values)); return; }
    setBusy(true);
    const outcome = await hub.run(script.name, project, fields.length > 0 ? args.json : null, startRequest(settings, choices.shown));
    setBusy(false);
    if (outcome.ok && outcome.sessionId) onStarted(outcome.sessionId);
    else setProblem(outcome.message ?? t("The script did not start."));
  }

  return <AppWindow storageKey="codealta.desktop.window.runScript.v1" className="workflow-run-dialog" titleId="workflow-run-title"
    title={t("Run {name}", { name: script.title ?? script.name })} preferredSize={viewport => ({ width: Math.min(560, viewport.width - 40), height: Math.min(350 + fields.reduce((sum, field) => sum + (field.kind === "json" ? 140 : 84), 0), viewport.height - 40) })}
    minimumSize={{ width: 420, height: 280 }} onClose={onClose} closeLabel={t("Close")} onOpened={() => first.current?.focus()} keepOnOutsidePress
    onCancel={event => { event.preventDefault(); onClose(); }} onKeyDown={event => event.stopPropagation()}>
    <form className="workflow-run" onSubmit={event => { event.preventDefault(); void run(); }}>
      <div className="workflow-run-body">
        {script.description && <p className="workflow-run-description">{script.description}</p>}
        {fields.map((field, index) => {
          const id = `workflow-argument-${field.name}`;
          const ref = index === 0 ? (element: HTMLElement | null) => { first.current = element; } : undefined;
          const value = values[field.name];
          const label = <span className="workflow-run-label"><code>{field.name}</code>{field.required && <span className="workflow-run-required" title={t("Required")} aria-label={t("Required")}>*</span>}</span>;
          if (field.kind === "boolean") return <FormGroup key={field.name} helperText={field.description ?? undefined}>
            <Checkbox id={id} inputRef={ref} checked={value === true} disabled={busy} labelElement={label} onChange={event => edit(field.name, event.currentTarget.checked)} />
          </FormGroup>;
          return <FormGroup key={field.name} label={label} labelFor={id} helperText={field.description ?? undefined}>
            {field.kind === "json"
              ? <TextArea id={id} inputRef={ref} fill className="workflow-run-json" value={typeof value === "string" ? value : ""} disabled={busy} spellCheck={false}
                  placeholder="[ ]" onChange={event => edit(field.name, event.target.value)} />
              : <InputGroup id={id} inputRef={ref} value={typeof value === "string" ? value : ""} disabled={busy} spellCheck={field.kind === "text"}
                  inputMode={field.kind === "integer" ? "numeric" : field.kind === "number" ? "decimal" : undefined} onChange={event => edit(field.name, event.target.value)} />}
          </FormGroup>;
        })}
        <FormGroup label={t("Runs in")} labelFor="workflow-run-project">
          <HTMLSelect id="workflow-run-project" fill value={project} disabled={busy} onChange={event => { setProject(event.target.value); setProblem(null); }}>
            {projects.length === 0 && <option value="">{t("Open a project first.")}</option>}
            {projects.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name}</option>)}
          </HTMLSelect>
        </FormGroup>
        {/* What its session runs with: its model runs the script, and is the one of the sessions it starts without a tier. */}
        {choices.provider && <FormGroup label={t("Runs with")} className="workflow-run-with">
          <SessionSettingsChip id="workflow-run" providers={providers} choices={choices} value={settings} disabled={busy} onChange={next => { setSettings(next); setProblem(null); }} />
        </FormGroup>}
        {problem && <Callout intent="danger" compact role="alert">{problem}</Callout>}
      </div>
      <footer className="workflow-run-footer">
        <Button onClick={onClose}>{t("Cancel")}</Button>
        <Button type="submit" intent="primary" loading={busy} disabled={!project} icon={<AppIcon name="play" size={14} />}>{t("Run")}</Button>
      </footer>
    </form>
  </AppWindow>;
}
