import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptWait } from "../timeline";
import { scriptWaitTarget, wakeTime } from "./scriptRunOffers";

/** The run waits for the user: the line says for what, and Open shows where it is answered in the pane of the session. A sleep says until when. */
export function ScriptWaitLine({ wait }: { wait: ScriptWait }) {
  const { t } = useShellLanguage();
  if (wait.kind === "sleep") {
    const until = wakeTime(wait.until);
    return <div className="script-run-waiting" role="status" data-kind="sleep">
      <AppIcon name="timer" size={13} />
      <span className="script-run-waiting-text"><strong>{until ? t("Sleeps until {time}", { time: until }) : t("Sleeps")}</strong></span>
    </div>;
  }

  const kind = wait.kind;
  const label = kind === "question" ? t("Waits for your answer") : t("Waits for your approval");
  return <div className="script-run-waiting" role="status">
    <AppIcon name={wait.kind === "question" ? "ask" : "shield"} size={13} />
    <span className="script-run-waiting-text"><strong>{label}</strong>{wait.kind === "question" && wait.text && <span>{wait.text}</span>}</span>
    <Button size="small" variant="minimal" text={t("Open")} onClick={event => {
      event.stopPropagation();
      const target = event.currentTarget.closest(".session-workspace")?.querySelector<HTMLElement>(scriptWaitTarget(kind));
      target?.scrollIntoView({ block: "nearest" });
      target?.querySelector<HTMLElement>("textarea, input, button")?.focus();
    }} />
  </div>;
}
