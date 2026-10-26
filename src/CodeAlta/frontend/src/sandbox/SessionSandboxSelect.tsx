import { HTMLSelect } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import { notSandboxed, sandboxProfileChoices } from "./sandboxChoices";

/**
 * The sandbox of a session: a row of the form of the Agent chip, as "Review before commit" is. The first entry follows
 * the settings (its model, its provider, its project, its space, every session) and says what they give; the others
 * give the session a choice of its own: not sandboxed, or a profile. A change applies from the next command.
 */
export function SessionSandboxSelect({ id, own, inherited, choices, disabled = false, onChange }: {
  id: string;
  /** The choice of the session (a profile or `off`); null when it follows the settings. */
  own: string | null;
  /** What the settings give: a profile, null for none; undefined while it is not known. */
  inherited: string | null | undefined;
  /** `off` and the profiles, as the host lists them. */
  choices: readonly string[];
  disabled?: boolean;
  /** Called with the new choice; null follows the settings. */
  onChange: (value: string | null) => void;
}) {
  const { t } = useShellLanguage();
  const profiles = sandboxProfileChoices(choices);
  const value = own ?? "";
  const shown = (profile: string | null) => profile === null || profile.toLowerCase() === notSandboxed ? t("Off") : profile;
  const effective = own !== null ? own : inherited;
  return <HTMLSelect fill id={id} className="composer-sandbox" disabled={disabled} value={value} data-own={own !== null || undefined}
    aria-label={`${t("Sandbox")}: ${effective === undefined ? t("Default") : shown(effective)}`}
    title={t("Where the commands and the file tools of this session run.")}
    onChange={event => {
      const next = event.target.value;
      if (next !== value) onChange(next === "" ? null : next);
    }}>
    <option value="">{inherited === undefined ? t("Default") : `${shown(inherited)} · ${t("Default")}`}</option>
    <option value={notSandboxed}>{t("Off")}</option>
    {profiles.map(profile => <option key={profile} value={profile}>{profile}</option>)}
    {own !== null && own.toLowerCase() !== notSandboxed && !profiles.some(profile => profile.toLowerCase() === own.toLowerCase())
      && <option value={own}>{own}</option>}
  </HTMLSelect>;
}
