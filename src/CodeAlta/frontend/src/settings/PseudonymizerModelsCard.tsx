import { useEffect, useRef, useState } from "react";
import { Button, Card, ProgressBar } from "@blueprintjs/core";
import { pseudonymizer } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { securityLanguageHelp, securityLanguageName } from "../explorer/projectSecurity";
import { usePseudonymizerModels } from "../explorer/usePseudonymizerModels";
import { useShellLanguage } from "../shellLanguage";

/** The key of the settings page of the built-in Pseudonymizer plugin. */
export const pseudonymizerPageKey = "builtin:pseudonymizer";

/** The calls of the host the card makes. */
export type PseudonymizerModelsApi = Pick<typeof pseudonymizer, "models" | "install" | "delete">;

/**
 * The languages of the documents and their models, on the page of the Pseudonymizer plugin, as the window of a project
 * shows them: each language with what it finds, its model and its size, downloaded or not, and what deletes it or
 * downloads it again. A project chooses its language in its window: this card frees the disk.
 */
export function PseudonymizerModelsCard({ epoch, api = pseudonymizer }: { epoch: string; api?: PseudonymizerModelsApi }) {
  const { t } = useShellLanguage();
  const [models, setModels] = usePseudonymizerModels(api, epoch);
  const [busy, setBusy] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);
  if (!models) return null;
  async function act(language: string, action: "install" | "delete") {
    setBusy(language); setProblem(null);
    try {
      const answer = await api[action]({ expectedEpoch: epoch, language }, { timeoutMilliseconds: 30_000 }).catch(() => null);
      if (!alive.current) return;
      if (answer?.status === "ok") setModels(answer);
      else setProblem(t(answer?.status === "busy" ? "This model is being downloaded: it can be deleted once the download ends." : "The model was not changed ({status}).", { status: answer?.status ?? "unavailable" }));
    } finally { if (alive.current) setBusy(null); }
  }
  const download = models.download;
  return <Card className="appearance-settings pseudonymizer-models" aria-labelledby="pseudonymizer-models-title">
    <h2 id="pseudonymizer-models-title">{t("Languages and their models")}</h2>
    <p className="settings-field-help">{t("A pseudonymized project chooses the language of its documents, and its names always or never hidden, in Pseudonymization, in the menu of the project. The model of a language is downloaded once, and runs on this computer.")}</p>
    <ul className="pseudonymizer-model-list">
      {models.languages.filter(item => item.model).map(item => {
        const ours = download?.state === "downloading" && download.language === item.id;
        return <li key={item.id}>
          <span className="pseudonymizer-model-language">
            <strong>{t(securityLanguageName(item.id))}</strong>
            <small>{t(securityLanguageHelp(item.id))}</small>
            <small className="pseudonymizer-model-meta">{t("{model} · {size} MB", { model: item.model ?? "", size: item.sizeMegabytes })}</small>
          </span>
          <span className="pseudonymizer-model-actions">{ours && download ? <span className="pseudonymizer-model-progress"><ProgressBar value={download.percent / 100} intent="primary" stripes={false} /><small>{download.percent} %</small></span>
            : item.installed ? <><span className="pseudonymizer-model-state" data-installed="true"><AppIcon name="check" size={12} />{t("Downloaded")}</span>
              <Button variant="minimal" intent="danger" size="small" text={t("Delete")} loading={busy === item.id} disabled={!!busy} onClick={() => void act(item.id, "delete")} /></>
            : <><span className="pseudonymizer-model-state">{t("Not downloaded")}</span>
              <Button variant="minimal" size="small" text={t("Download the model")} loading={busy === item.id} disabled={!!busy} onClick={() => void act(item.id, "install")} /></>}</span>
        </li>;
      })}
    </ul>
    {problem && <p className="project-security-warning" role="alert">{problem}</p>}
  </Card>;
}
