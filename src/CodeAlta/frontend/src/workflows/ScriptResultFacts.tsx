import { useContext } from "react";
import { AppIcon } from "../AppIcon";
import { MarkdownContent } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import { OpenRunContext } from "./openRun";
import type { ScriptResult } from "./scriptResult";

/**
 * What a card of the result of a script says beside the result itself: how the run ended, the file that keeps the whole
 * of a long result, the tab of the run, and what the run wrote (its output, its profile, an error), folded when there is
 * a result to read.
 */
export function ScriptResultFacts({ result, run, onOpenLink }: {
  result: ScriptResult;
  /** The run, for its tab, which reads the whole text of the result; null where the result is already in that tab. */
  run?: Readonly<{ runId: string; title: string | null }> | null;
  /** Opens the file of the whole result; null where nothing opens a file. */
  onOpenLink: ((address: string) => void) | null;
}) {
  const { t } = useShellLanguage();
  const openRun = useContext(OpenRunContext);
  const full = result.fullResult;
  return <div className="script-result-facts" data-phase={result.phase}>
    <div className="script-result-status">
      <span className="script-result-outcome">{result.status}</span>
      {full && onOpenLink && <button type="button" className="quiet-button script-result-open"
        title={t("Only the start and the end of {characters} characters are shown.", { characters: full.characters })}
        onClick={() => onOpenLink(full.path)}><AppIcon name="file" size={13} />{t("Open the full result")}</button>}
      {run && openRun && <button type="button" className="quiet-button script-result-open"
        title={t("Open the run in a tab: its graph, its sessions, its timeline and its script.")}
        onClick={() => openRun(run)}><AppIcon name="expand" size={13} />{t("Open run")}</button>}
    </div>
    {result.notes && result.result !== null && <details className="script-result-notes">
      <summary>{t("Run details")}</summary>
      <MarkdownContent source={result.notes} timelineCodeBlocks onOpenLink={onOpenLink} />
    </details>}
  </div>;
}
