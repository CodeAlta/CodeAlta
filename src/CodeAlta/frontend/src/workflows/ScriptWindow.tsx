import { useEffect, useState, type ReactNode } from "react";
import { Button } from "@blueprintjs/core";
import type { WorkflowScriptItem } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { ScriptSource } from "./ScriptSource";
import { scopeLabel } from "./workflows";
import type { WorkflowsHub } from "./workflowsHub";

/**
 * The script of a card of the library, read in a window of its own, large: what it does (its description, its steps and
 * its parameters, as its card shows them), then its source in the colors of JavaScript with the numbers of its lines,
 * where it is, and what can be done with it (Edit in the code editor, Run…).
 */
export function ScriptWindow({ hub, script, projectId, facts, onClose, onEdit, onRun }: {
  hub: WorkflowsHub; script: WorkflowScriptItem;
  /** The project whose library the script is read from; null for yours and the samples. */
  projectId: string | null;
  /** What the script does, as its card shows it: drawn above its source. */
  facts: ReactNode;
  onClose: () => void;
  /** Opens the script in the code editor; null for a sample, which is not edited. */
  onEdit: (() => void) | null;
  onRun: (() => void) | null;
}) {
  const { t } = useShellLanguage();
  // Undefined until it is read; null when it cannot be read.
  const [source, setSource] = useState<string | null | undefined>(undefined);
  useEffect(() => {
    const abort = new AbortController();
    setSource(undefined);
    void hub.source(script.name, script.scope, projectId, abort.signal).then(value => { if (!abort.signal.aborted) setSource(value); });
    return () => abort.abort();
  }, [hub, script.name, script.scope, projectId]);
  return <AppWindow storageKey="codealta.desktop.window.workflowScript.v1" className="workflow-script-dialog" titleId="workflow-script-title"
    title={script.title ?? script.name} preferredSize={viewport => ({ width: Math.min(1100, viewport.width - 80), height: Math.min(820, viewport.height - 80) })}
    minimumSize={{ width: 480, height: 320 }} onClose={onClose} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); onClose(); }}>
    <div className="workflow-script-window">
      <p className="workflow-script-where">
        <code>{script.name}</code>
        <span className="workflow-scope" data-scope={script.scope}>{t(scopeLabel(script.scope))}</span>
        <span className="workflow-script-path" title={script.path}>{script.path}</span>
      </p>
      <div className="workflow-script-facts">{facts}</div>
      {source === undefined ? <p className="automation-empty"><ActivitySpinner size={16} /></p>
        : source === null ? <p className="workflow-card-problem" role="alert">{t("The script cannot be read.")}</p>
        : <ScriptSource source={source} className="workflow-script-window-source" />}
      <footer className="workflow-run-footer">
        {onEdit && <Button icon={<AppIcon name="edit" size={14} />} onClick={() => { onEdit(); onClose(); }}>{t("Edit")}</Button>}
        {onRun && <Button intent="primary" icon={<AppIcon name="play" size={14} />} onClick={onRun}>{t("Run…")}</Button>}
        <Button onClick={onClose}>{t("Close")}</Button>
      </footer>
    </div>
  </AppWindow>;
}
