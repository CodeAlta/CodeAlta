import { HTMLSelect } from "@blueprintjs/core";
import { useShellLanguage } from "./shellLanguage";

/**
 * Whether the user reviews the changes of a session before an agent commits them: a row of the form of the Agent chip.
 * The first entry follows the setting (Settings → Sessions) and says what it is; the others give the session a
 * choice of its own. A change applies at once: the agent is told with its next prompt, and a commit is refused
 * until a review approves the work tree.
 */
export function ReviewBeforeCommitSelect({ id, own, setting, disabled = false, onChange }: {
  id: string;
  /** The choice of the session; null when it follows the setting. */
  own: boolean | null;
  /** The setting, which applies when the session has no choice of its own. */
  setting: boolean;
  disabled?: boolean;
  /** Called with the new choice; null follows the setting. */
  onChange: (value: boolean | null) => void;
}) {
  const { t } = useShellLanguage();
  const value = own === null ? "" : own ? "on" : "off";
  const effective = own ?? setting;
  return <HTMLSelect fill id={id} className="composer-review" disabled={disabled} value={value} data-own={own !== null || undefined}
    aria-label={`${t("Review before commit")}: ${t(effective ? "On" : "Off")}`}
    title={t("An agent asks for your review of its changes and commits only what you approve.")}
    onChange={event => {
      const next = event.target.value;
      if (next !== value) onChange(next === "" ? null : next === "on");
    }}>
    <option value="">{`${t(setting ? "On" : "Off")} · ${t("Default")}`}</option>
    <option value="on">{t("On")}</option>
    <option value="off">{t("Off")}</option>
  </HTMLSelect>;
}
