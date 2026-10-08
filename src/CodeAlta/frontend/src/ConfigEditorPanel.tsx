import { useEffect, useRef, useState } from "react";
import { Button, Callout, NonIdealState, Tag } from "@blueprintjs/core";
import { globalConfig, type GlobalConfigValidationResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { CodeEditor } from "./monaco/CodeEditor";
import { canSaveConfig, configReadNotice, configSaveNotice, maximumConfigLength, type ConfigBaseline, type ConfigNotice } from "./configEditor";
import { SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import type { SettingsProject } from "./SettingsPage";
import { useShellLanguage } from "./shellLanguage";

/** Settings page that edits the global config.toml: validated as you type, saved only against the revision that was read. */
export function ConfigEditorPanel({ epoch, project = null, api = globalConfig, onApplied, onOpenFile, filesApi }: {
  epoch: string | null; api?: Pick<typeof globalConfig, "read" | "validate" | "save">;
  /** The selected project: its own configuration file is shown beside the global one, and opened in the code editor. */
  project?: SettingsProject;
  /** Called once the code editor was asked to show a configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the configuration files are and opens them; the host by default. */
  filesApi?: SettingsFilesApi;
  /** Called after providers were re-registered so provider views can refresh. */
  onApplied?: () => void;
}) {
  const { t } = useShellLanguage();
  const [baseline, setBaseline] = useState<ConfigBaseline | null>(null);
  const [content, setContent] = useState("");
  const [validation, setValidation] = useState<GlobalConfigValidationResponse | null>(null);
  const [notice, setNotice] = useState<ConfigNotice | null>(null);
  const [diagnostic, setDiagnostic] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const files = useSettingsFiles({ page: "config", epoch, projectId: project?.id ?? null, revision: baseline, onOpened: onOpenFile, api: filesApi,
    setNotice: value => setNotice(value && { key: value.key, intent: value.intent }) });

  // Read on open and on an explicit reload; a newer read supersedes a slower one.
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setNotice(null); setDiagnostic(null);
    void api.read({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      const failure = configReadNotice(value.status);
      if (failure || value.content === null || value.revision === null) { setBaseline(null); setNotice(failure ?? configReadNotice("read_failed")); }
      else { setBaseline({ content: value.content, revision: value.revision }); setContent(value.content); setValidation(null); }
    }).catch(() => { if (!controller.signal.aborted) { setBaseline(null); setNotice(configReadNotice("read_failed")); } })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [api, epoch, generation]);

  // Validate shortly after typing stops; a result for older text is dropped.
  useEffect(() => {
    if (!baseline) return;
    setValidation(null);
    if (content.length > maximumConfigLength) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void api.validate({ content }, { signal: controller.signal, timeoutMilliseconds: 15000 })
        .then(value => { if (!controller.signal.aborted) setValidation(value); })
        .catch(() => { /* A missed validation leaves Save disabled until the next edit or attempt. */ });
    }, 350);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [api, baseline, content]);

  async function save(applyProviders: boolean) {
    if (!baseline || !canSaveConfig(baseline, content, validation, busy)) return;
    const submitted = content;
    setBusy(true); setNotice(null); setDiagnostic(null);
    try {
      const result = await api.save({ expectedEpoch: epoch, content: submitted, expectedRevision: baseline.revision, applyProviders }, { timeoutMilliseconds: 60000 });
      if (!alive.current) return;
      setNotice(configSaveNotice(result, applyProviders));
      setDiagnostic(result.message);
      if ((result.status === "ok" || result.status === "apply_failed") && result.revision) {
        setBaseline({ content: submitted, revision: result.revision });
        if (applyProviders && result.status === "ok") onApplied?.();
      }
    } catch {
      // The outcome is unknown: the file may or may not have been written.
      if (alive.current) setNotice({ key: "The save did not complete; reload to see what is on disk.", intent: "danger" });
    } finally { if (alive.current) setBusy(false); }
  }

  const dirty = !!baseline && content !== baseline.content;
  const savable = canSaveConfig(baseline, content, validation, busy);
  const marker = validation && !validation.valid && validation.line
    ? { line: validation.line, column: validation.column ?? 1, message: validation.message ?? "" } : null;
  return <section className="config-editor configuration-page" aria-labelledby="config-editor-title">
    <header className="page-heading config-editor-heading">
      <div><span className="eyebrow">{t("Advanced")}</span><h1 id="config-editor-title">{t("Configuration file")}</h1>
        <p>{t("Global config.toml: providers and their credentials, the default provider, plugins and skills.")}</p></div>
      <div className="config-editor-actions">
        {busy && <ActivitySpinner size={14} />}
        {dirty && <Tag minimal intent="warning">{t("Unsaved changes")}</Tag>}
        <Button icon={<AppIcon name="refresh" size={15} />} disabled={loading || busy} onClick={() => setGeneration(value => value + 1)}
          title={t("Discard edits and read the file again")}>{t("Reload")}</Button>
        <Button disabled={!savable} onClick={() => void save(false)}>{t("Save")}</Button>
        <Button intent="primary" disabled={!savable} onClick={() => void save(true)}
          title={t("Saves, then re-registers the configured providers in this running app.")}>{t("Save and apply providers")}</Button>
      </div>
    </header>
    <SettingsFileLocations files={files} disabled={busy} />
    {notice && <Callout intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>
      {t(notice.key, notice.parameters)}{diagnostic && <div className="config-editor-diagnostic">{diagnostic}</div>}</Callout>}
    {baseline && validation && !validation.valid && <Callout intent="danger" compact role="alert">
      {validation.line ? t("Line {line}, column {column}", { line: validation.line, column: validation.column ?? 1 }) : t("Invalid configuration")}
      {validation.message && <div className="config-editor-diagnostic">{validation.message}</div>}</Callout>}
    {/* A file that loads may still leave something out: the providers of a version newer than this one. */}
    {baseline && validation?.valid && validation.warning && <Callout intent="warning" compact role="status">
      <div className="config-editor-diagnostic">{validation.warning}</div></Callout>}
    {baseline && content.length > maximumConfigLength && <Callout intent="danger" compact role="alert">{t("The configuration is too large for this editor.")}</Callout>}
    {loading && !baseline ? <NonIdealState icon={<ActivitySpinner size={28} />} title={t("Reading configuration…")} />
      : baseline ? <div className="config-editor-surface"><CodeEditor value={content} onChange={setContent} language="ini" label={t("Configuration file")} readOnly={busy} marker={marker} /></div>
      : <NonIdealState icon={<AppIcon name="settings" size={36} />} title={t("Configuration unavailable")}
        action={<Button icon={<AppIcon name="refresh" size={15} />} onClick={() => setGeneration(value => value + 1)}>{t("Reload")}</Button>} />}
  </section>;
}
