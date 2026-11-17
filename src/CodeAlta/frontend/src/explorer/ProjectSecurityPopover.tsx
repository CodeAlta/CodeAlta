import { useEffect, useRef, useState } from "react";
import { Button, PopoverNext, ProgressBar, Radio, RadioGroup, TextArea } from "@blueprintjs/core";
import { plugins, pseudonymizer, settings, workspace, type PseudonymizerLanguageEntry, type WorkspaceProject } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import {
  defaultSecurityLanguage, downloading, makeProjectNormal, readProjectSecurity, saveProjectSecurity, secureProject, securityLanguageHelp, securityLanguageName, securityLines,
  type ProjectSecurityApi, type ProjectSecurityChoice,
} from "./projectSecurity";
import { usePseudonymizerModels } from "./usePseudonymizerModels";

/** The calls of the host the popover makes. */
export const projectSecurityApi: ProjectSecurityApi = {
  setProjectSecure: workspace.setProjectSecure, setEnabled: plugins.setEnabled, getSetting: settings.get, setSetting: settings.set,
  models: pseudonymizer.models, install: pseudonymizer.install,
};

/**
 * The security of a project, in a popover beside its row: a normal project is made secure there, with the language of
 * its documents, which chooses the model that finds names (downloaded once, when it is missing); a secure project
 * changes its choices there, or becomes normal again. The download goes on once it is closed.
 */
export function ProjectSecurityPopover({ epoch, project, api, onClose, onChanged }: {
  epoch: string; project: WorkspaceProject; api: ProjectSecurityApi; onClose: () => void;
  /** Called once the project became secure or normal: the projects are read again for its padlock. */
  onChanged: (secure: boolean) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [secure, setSecure] = useState(!!project.secure);
  const [models, setModels] = usePseudonymizerModels(api, epoch);
  const [saved, setSaved] = useState<ProjectSecurityChoice | null>(null);
  const [language, setLanguage] = useState<string>(defaultSecurityLanguage(locale));
  const [alwaysHide, setAlwaysHide] = useState("");
  const [neverHide, setNeverHide] = useState("");
  const [busy, setBusy] = useState(false);
  const [asking, setAsking] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);

  // What the project chose, when it is secure.
  useEffect(() => {
    if (!project.secure) return;
    void readProjectSecurity(api, epoch, project.id).then(choice => {
      if (!alive.current || !choice) return;
      setSaved(choice); setLanguage(choice.language);
      setAlwaysHide(choice.alwaysHide.join("\n")); setNeverHide(choice.neverHide.join("\n"));
    });
  }, [api, epoch, project.id, project.secure]);
  const running = downloading(models);

  const entry = models?.languages.find(item => item.id === language) ?? null;
  const missing = entry ? !entry.installed : false;
  const choice: ProjectSecurityChoice = { language, alwaysHide: securityLines(alwaysHide), neverHide: securityLines(neverHide) };
  const changed = !saved || saved.language !== choice.language || saved.alwaysHide.join("\n") !== choice.alwaysHide.join("\n")
    || saved.neverHide.join("\n") !== choice.neverHide.join("\n");
  const failure = (status: string) => t(status === "plugin_failed" ? "The Pseudonymizer plugin could not be turned on: nothing was changed."
    : status === "settings_failed" ? "The choices of the project could not be saved: nothing was changed." : "The project was not changed ({status}).", { status });

  async function run(action: () => Promise<{ status: string }>, after: () => void) {
    setBusy(true); setProblem(null);
    try {
      const outcome = await action();
      if (!alive.current) return;
      if (outcome.status !== "ok") { setProblem(failure(outcome.status)); return; }
      after();
      const answer = await api.models({ expectedEpoch: epoch }, { timeoutMilliseconds: 10_000 }).catch(() => null);
      if (alive.current && answer?.status === "ok") setModels(answer);
    } finally { if (alive.current) setBusy(false); }
  }
  const secureIt = () => void run(() => secureProject(api, epoch, project.id, choice), () => { setSecure(true); setSaved(choice); onChanged(true); });
  const save = () => void run(() => saveProjectSecurity(api, epoch, project.id, choice), () => setSaved(choice));
  const normal = () => void run(() => makeProjectNormal(api, epoch, project.id), () => { onChanged(false); onClose(); });

  const download = models?.download ?? null;
  const ours = running && download?.language === language;
  // The model of the language the project has saved: while it is missing, its sessions send nothing. A language only
  // chosen here says its download on the button.
  const savedLanguage = secure ? saved?.language ?? null : null;
  const savedMissing = !!savedLanguage && models?.languages.find(item => item.id === savedLanguage)?.installed === false
    && !(running && download?.language === savedLanguage);
  const form = <div className="project-security-form" role="dialog" aria-label={t(secure ? "Pseudonymization of {name}" : "Pseudonymize {name}", { name: project.name })}
    onKeyDown={event => { if (event.key === "Escape" && !busy) { event.preventDefault(); event.stopPropagation(); onClose(); } }}>
    <header><AppIcon name="lock" size={16} /><strong>{t(secure ? "Pseudonymization of {name}" : "Pseudonymize {name}", { name: project.name })}</strong></header>
    <p className="project-security-lead">{t("What its sessions send to their provider is pseudonymized: names, identifiers, contact details. The real values stay on this computer, and the answers get them back.")}</p>
    {!models ? <ActivitySpinner size={16} /> : <>
      <RadioGroup label={t("Language of the documents")} className="project-security-languages" selectedValue={language} disabled={busy}
        onChange={event => setLanguage(event.currentTarget.value)}>
        {models.languages.map(item => <Radio key={item.id} value={item.id} labelElement={<LanguageLabel item={item} />} />)}
      </RadioGroup>
      <details className="project-security-lists" open={!!saved && (saved.alwaysHide.length > 0 || saved.neverHide.length > 0)}>
        <summary>{t("Names and words")}</summary>
        <label>{t("Names always hidden")}<small>{t("The parties of the file, even where nothing finds them. One per line.")}</small>
          <TextArea fill rows={3} value={alwaysHide} disabled={busy} onChange={event => setAlwaysHide(event.currentTarget.value)} placeholder="Sylvie Durand" /></label>
        <label>{t("Words never hidden")}<small>{t("Sent as they are: what was taken for personal data by mistake. One per line.")}</small>
          <TextArea fill rows={2} value={neverHide} disabled={busy} onChange={event => setNeverHide(event.currentTarget.value)} /></label>
      </details>
      {ours && download && <div className="project-security-download" role="status">
        <span>{t("Downloading {model}… {percent} %", { model: entry?.model ?? "", percent: download.percent })}</span>
        <ProgressBar value={download.percent / 100} intent="primary" stripes={false} />
        <small>{t("The download goes on once this is closed. Until it ends, the sessions of this project send nothing.")}</small>
      </div>}
      {savedMissing && <p className="project-security-warning" role="status">{download?.state === "failed" && download.language === savedLanguage
        ? t("The download failed ({error}). The sessions of this project send nothing until the model is downloaded.", { error: download.error ?? "" })
        : t("This model is not downloaded: the sessions of this project send nothing until it is.")}</p>}
    </>}
    {problem && <p className="project-security-warning" role="alert">{problem}</p>}
    {asking ? <div className="project-security-confirm" role="alert">
      <p>{t("Stop pseudonymizing {name}? Its next sessions send what they hold as it is. The sessions that were confidential stay so.", { name: project.name })}</p>
      <div className="rename-actions">
        <Button variant="minimal" text={t("Keep pseudonymizing")} disabled={busy} onClick={() => setAsking(false)} />
        <Button intent="danger" text={t("Stop pseudonymizing")} loading={busy} onClick={normal} />
      </div>
    </div> : <div className="project-security-actions">
      <Button variant="minimal" text={t(secure && !changed ? "Close" : "Cancel")} disabled={busy} onClick={onClose} />
      {!secure ? <Button intent="primary" icon={<AppIcon name="lock" size={14} />} loading={busy} disabled={!models}
        text={missing && entry ? t("Pseudonymize and download {size} MB", { size: entry.sizeMegabytes }) : t("Pseudonymize")} onClick={secureIt} />
        : (changed || (missing && !ours)) && <Button intent="primary" loading={busy} disabled={!models}
          text={missing && !ours && entry ? t("Save and download {size} MB", { size: entry.sizeMegabytes }) : t("Save")} onClick={save} />}
    </div>}
    {secure && !asking && <footer className="project-security-footer">
      <Button variant="minimal" intent="danger" size="small" text={t("Stop pseudonymizing…")} disabled={busy} onClick={() => setAsking(true)} />
    </footer>}
  </div>;
  return <PopoverNext isOpen content={form} placement="right-start" popoverClassName="rename-popover project-security-popover" className="rename-anchor" targetTagName="span"
    autoFocus enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={next => { if (!next && !busy) onClose(); }}>
    <span aria-hidden="true" />
  </PopoverNext>;
}

// A language and its model: its size, or that it is downloaded already.
function LanguageLabel({ item }: { item: PseudonymizerLanguageEntry }) {
  const { t } = useShellLanguage();
  return <span className="project-security-language">
    <span><strong>{t(securityLanguageName(item.id))}</strong><small>{t(securityLanguageHelp(item.id))}</small></span>
    <span className="project-security-model" data-installed={item.installed}>
      {!item.model ? t("No download") : item.installed ? <><AppIcon name="check" size={12} />{item.model}</> : t("{model} · {size} MB", { model: item.model, size: item.sizeMegabytes })}
    </span>
  </span>;
}
