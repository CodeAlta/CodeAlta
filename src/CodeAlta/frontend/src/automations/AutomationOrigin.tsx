import { createContext } from "react";
import { useShellLanguage } from "../shellLanguage";

/** The automation that started the session of a timeline, and what started this run. */
export type AutomationOrigin = Readonly<{
  /** The name of the automation; null when it no longer exists. */
  name: string | null;
  /** What started this run, on one line; null when the page no longer knows the run. */
  summary: string | null;
  /** Opens the automation; absent when it no longer exists or the window owns no host. */
  onOpen?: () => void;
}>;

/** The automation that started the session whose timeline is shown; null for a session the user started. */
export const AutomationOriginContext = createContext<AutomationOrigin | null>(null);

/**
 * Names, on the card of the prompt an automation sent, that automation and what started the run, with the way back
 * to it: the session itself looks like any other one.
 */
export function AutomationOriginLabel({ name, summary, onOpen }: AutomationOrigin) {
  const { t } = useShellLanguage();
  return <small className="automation-origin">
    {name === null ? t("This automation no longer exists")
      : onOpen ? <button type="button" className="automation-origin-name" title={t("Open the automation")} onClick={onOpen}>{name}</button>
      : <strong>{name}</strong>}
    {name !== null && summary ? ` · ${summary}` : ""}
  </small>;
}
