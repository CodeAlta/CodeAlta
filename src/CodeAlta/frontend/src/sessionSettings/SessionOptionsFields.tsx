import { FormGroup, Switch } from "@blueprintjs/core";
import type { SessionOptionsChoice } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { sessionOptionsOf, storedSessionOptions } from "./sessionOptions";

/**
 * The options of a session in the form of its Agent chip, under More options: a switch per option with what it does in
 * one line. A change gives the options the session records (null for the defaults).
 */
export function SessionOptionsFields({ id, value, disabled = false, onChange }: {
  /** Tells the controls of this form apart from the others of the page. */
  id: string;
  value: SessionOptionsChoice | null;
  disabled?: boolean;
  onChange: (value: SessionOptionsChoice | null) => void;
}) {
  const { t } = useShellLanguage();
  const options = sessionOptionsOf(value);
  return <FormGroup className="composer-selection-option" labelFor={`composer-sub-sessions-${id}`}
    helperText={t(options.subSessions ? "The agent can start sessions of its own (sub-agents)." : "The agent does all the work itself: it starts no session.")}>
    <Switch id={`composer-sub-sessions-${id}`} checked={options.subSessions} disabled={disabled}
      labelElement={<span className="composer-selection-label"><AppIcon name="subAgents" size={14} />{t("Sub-sessions")}</span>}
      onChange={event => onChange(storedSessionOptions({ ...options, subSessions: event.currentTarget.checked }))} />
  </FormGroup>;
}
