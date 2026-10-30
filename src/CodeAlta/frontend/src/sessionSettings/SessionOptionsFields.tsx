import { FormGroup, HTMLSelect, Switch } from "@blueprintjs/core";
import type { SessionOptionsChoice } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { compactTokens } from "../contextUsage";
import { useShellLanguage } from "../shellLanguage";
import { sessionOptionsOf, storedSessionOptions, type ModelContextSizes } from "./sessionOptions";

/**
 * The options of a session in the form of its Agent chip, under More options: a switch per option with what it does in
 * one line, and the size of its context where its model has several. A change gives the options the session records
 * (null for the defaults).
 */
export function SessionOptionsFields({ id, value, context = null, disabled = false, onChange }: {
  /** Tells the controls of this form apart from the others of the page. */
  id: string;
  value: SessionOptionsChoice | null;
  /** The model of the session, whose sizes of context are offered where it has several. */
  context?: ModelContextSizes | null;
  disabled?: boolean;
  onChange: (value: SessionOptionsChoice | null) => void;
}) {
  const { t } = useShellLanguage();
  const options = sessionOptionsOf(value);
  return <>
    <FormGroup className="composer-selection-option" labelFor={`composer-sub-sessions-${id}`}
      helperText={t(options.subSessions ? "The agent can start sessions of its own (sub-agents)." : "The agent does all the work itself: it starts no session.")}>
      <Switch id={`composer-sub-sessions-${id}`} checked={options.subSessions} disabled={disabled}
        labelElement={<span className="composer-selection-label"><AppIcon name="subAgents" size={14} />{t("Sub-sessions")}</span>}
        onChange={event => onChange(storedSessionOptions({ ...options, subSessions: event.currentTarget.checked }))} />
    </FormGroup>
    {!!context?.contextSizes?.length && <FormGroup label={t("Context size")} labelFor={`composer-context-size-${id}`} helperText={t("The conversation is compacted before it exceeds this size.")}>
      <ContextSizeSelect id={`composer-context-size-${id}`} value={options.contextSize ?? null} context={context} session disabled={disabled}
        onChange={contextSize => onChange(storedSessionOptions({ ...options, contextSize }))} />
    </FormGroup>}
  </>;
}

/**
 * A size of context among those of a model: Default, with the size it stands for, then each size, as "272k" or "1M".
 * The Default of a session follows its provider and reads as the other rows of its form ("872k · Default"); the one of
 * a provider is the size of the model ("Default (272k)").
 */
export function ContextSizeSelect({ id, value, context, session = false, disabled = false, onChange }: {
  id: string; value: number | null; context: ModelContextSizes; session?: boolean; disabled?: boolean; onChange: (value: number | null) => void;
}) {
  const { t } = useShellLanguage();
  // A size chosen for another model stays shown, as the provider form shows a model it does not list.
  const sizes = [...new Set([...context.contextSizes ?? [], ...(value ? [value] : [])])].sort((left, right) => left - right);
  return <HTMLSelect fill id={id} value={value ?? ""} disabled={disabled} onChange={event => onChange(event.target.value ? Number(event.target.value) : null)}>
    <option value="">{!context.contextSize ? t("Default") : session ? `${compactTokens(String(context.contextSize))} · ${t("Default")}`
      : t("Default ({value})", { value: compactTokens(String(context.contextSize)) })}</option>
    {sizes.map(size => <option key={size} value={size}>{compactTokens(String(size))}</option>)}
  </HTMLSelect>;
}
