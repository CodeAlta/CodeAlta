import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/** Says, above the timeline of a session, which automation started it, with the way back to that automation. */
export function SessionOrigin({ name, summary, onOpen }: {
  /** The name of the automation; null when it no longer exists. */
  name: string | null;
  /** What started this run, on one line; null when the page no longer knows the run. */
  summary: string | null;
  onOpen?: () => void;
}) {
  const { t } = useShellLanguage();
  return <p className="session-origin">
    <AppIcon name="automation" size={14} />
    <span>{name ? <>{t("Started by the automation")} <strong>{name}</strong>{summary ? ` · ${summary}` : ""}</> : t("Started by an automation that no longer exists")}</span>
    {onOpen && <Button size="small" variant="minimal" endIcon={<AppIcon name="chevronRight" size={13} />} onClick={onOpen}>{t(name ? "Open the automation" : "Automations")}</Button>}
  </p>;
}
