// The prompts an agent prompt inherits, in the editor of the Agent prompts page of Settings: a chain of the inherited
// prompts in the order of their text, which ends with the prompt itself, the button that inherits one more, and the
// button that shows the text the agent receives (ComposedPromptDialog).
import { useEffect, useState } from "react";
import { Button, Menu, MenuItem, PopoverNext, Tag } from "@blueprintjs/core";
import type { AgentPromptComposeResponse, AgentPromptEntry, agentPrompts } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { scopeLabel } from "../PromptTags";
import { useShellLanguage } from "../shellLanguage";
import { inheritCandidates, maximumInherits, movedEarlier, promptInEffect, tokenCount, withInherited } from "./inheritance";

/** What the host composes for a prompt that inherits: its parts, what they set, and the text. */
export type PromptComposition = AgentPromptComposeResponse;

/**
 * Asks the host what a prompt is made of, a moment after its inherited prompts or its text last changed, so that the
 * page shows the prompt as it would be saved. Null while it inherits nothing, and when the host cannot tell.
 */
export function usePromptComposition({ api, epoch, projectId, id, inherits, body, disabledTools, enabled }: {
  api: Pick<typeof agentPrompts, "compose">; epoch: string | null; projectId: string | null;
  /** The id of the prompt; null for one not saved yet. */
  id: string | null; inherits: readonly string[]; body: string;
  /** The tools the prompt turns off itself. */
  disabledTools: readonly string[]; enabled: boolean;
}): PromptComposition | null {
  const [composition, setComposition] = useState<PromptComposition | null>(null);
  const key = enabled && inherits.length > 0 ? JSON.stringify([epoch, projectId, id, inherits, body, disabledTools]) : null;
  useEffect(() => {
    if (key === null) { setComposition(null); return; }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      api.compose({ expectedEpoch: epoch, projectId, id, inherits: [...inherits], body, disabledTools: [...disabledTools] }, { signal: controller.signal, timeoutMilliseconds: 20000 })
        .then(value => { if (!controller.signal.aborted) setComposition(value.status === "ok" ? value : null); })
        .catch(() => { if (!controller.signal.aborted) setComposition(null); });
    }, 250);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [key]);
  return composition;
}

/** The chain of the inherited prompts of an agent prompt, which the editor changes unless it is read-only. */
export function PromptInheritance({ promptId, inherits, prompts, composition, disabled = false, readOnly = false, onChange, onShow, onPreview }: {
  /** The id of the prompt, which it cannot inherit; empty for one not named yet. */
  promptId: string;
  inherits: readonly string[];
  /** The prompt files the page lists, from which the inherited ones are chosen. */
  prompts: readonly AgentPromptEntry[];
  composition: PromptComposition | null;
  disabled?: boolean; readOnly?: boolean;
  onChange: (inherits: readonly string[]) => void;
  /** Shows an inherited prompt on the page. */
  onShow: (prompt: AgentPromptEntry) => void;
  /** Shows the text the agent receives. */
  onPreview: () => void;
}) {
  const { t } = useShellLanguage();
  const candidates = inheritCandidates(prompts, promptId, inherits);
  const editable = !readOnly && !disabled;
  const missing = new Set((composition?.missing ?? []).map(id => id.toLowerCase()));
  const circular = new Set((composition?.circular ?? []).map(id => id.toLowerCase()));
  const parts = composition?.parts ?? [];
  return <div className="prompt-inheritance" role="group" aria-label={t("Inherits from")}>
    {inherits.length === 0 ? <span className="prompt-inheritance-none">{t("None")}</span>
      : <ol className="prompt-inheritance-chain">
        {inherits.map((id, index) => {
          const entry = promptInEffect(prompts, id), label = entry?.name || id;
          const state = missing.has(id.toLowerCase()) ? "missing" : circular.has(id.toLowerCase()) ? "circular" : undefined;
          return <li key={id} className="prompt-inheritance-step" data-state={state}>
            <span className="prompt-inheritance-index" aria-hidden="true">{index + 1}</span>
            {entry ? <button type="button" className="prompt-inheritance-name" title={t("Show {name}", { name: label })} disabled={disabled} onClick={() => onShow(entry)}>
              <strong>{label}</strong>{label !== id && <code>{id}</code>}</button>
              : <span className="prompt-inheritance-name"><code>{id}</code></span>}
            {entry && <small className="prompt-inheritance-scope">{t(scopeLabel(entry.scope))}</small>}
            {state === "missing" && <Tag minimal round intent="danger" title={t("No agent prompt has this id.")}>{t("Not found")}</Tag>}
            {state === "circular" && <Tag minimal round intent="warning" title={t("It inherits this prompt: it is left out.")}>{t("Circular")}</Tag>}
            {editable && index > 0 && <Button variant="minimal" size="small" icon={<AppIcon name="arrowLeft" size={13} />}
              aria-label={t("Move {name} earlier", { name: label })} title={t("Move {name} earlier", { name: label })} onClick={() => onChange(movedEarlier(inherits, index))} />}
            {editable && <Button variant="minimal" size="small" icon={<AppIcon name="close" size={13} />}
              aria-label={t("Stop inheriting {name}", { name: label })} title={t("Stop inheriting {name}", { name: label })} onClick={() => onChange(inherits.filter(other => other !== id))} />}
          </li>;
        })}
        <li className="prompt-inheritance-step" data-self="true"><span className="prompt-inheritance-name"><strong>{t("This prompt")}</strong></span></li>
      </ol>}
    {editable && <PopoverNext placement="bottom-start" disabled={candidates.length === 0 || inherits.length >= maximumInherits} content={<Menu className="prompt-inheritance-menu" aria-label={t("Inherit a prompt")}>
      {candidates.map(candidate => <MenuItem key={candidate.id} text={<span className="prompt-inheritance-choice"><strong>{candidate.name}</strong>
        {candidate.description && <small>{candidate.description}</small>}</span>} label={candidate.id} onClick={() => onChange(withInherited(inherits, candidate.id))} />)}
    </Menu>}>
      <Button variant="minimal" size="small" className="prompt-inheritance-add" icon={<AppIcon name="plus" size={13} />} text={t("Inherit a prompt")}
        disabled={candidates.length === 0 || inherits.length >= maximumInherits} />
    </PopoverNext>}
    {composition && inherits.length > 0 && <span className="prompt-inheritance-summary">
      <AppIcon name="inherit" size={13} />
      <span>{t("{count} prompts · about {tokens} tokens", { count: String(parts.length + 1), tokens: tokenCount(composition.tokens) })}</span>
      <Button variant="minimal" size="small" icon={<AppIcon name="eye" size={13} />} text={t("Preview the composed prompt")} onClick={onPreview} />
    </span>}
  </div>;
}
