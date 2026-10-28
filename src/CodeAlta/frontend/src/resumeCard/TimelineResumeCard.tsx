import { createContext, useContext, useSyncExternalStore } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { noResume, resumeLine, type ResumeHub, type ResumeOffer } from "./resumeHub";

/** The resume the session of a timeline waits for, given to its cards by the window; null where nothing can resume (a demo, a fixture). */
export const SessionResumeContext = createContext<ResumeHub | null>(null);

const noSubscription = () => () => {};

/**
 * The line of the warning card that scheduled a resume after a limit: when, with which provider, Resume now and Cancel.
 * It shows while the host says that this resume still waits; a resume that started, was cancelled or was replaced
 * leaves the card as the journal wrote it.
 */
export function TimelineResumeCard({ offer }: { offer: ResumeOffer }) {
  const { t, locale } = useShellLanguage();
  const hub = useContext(SessionResumeContext);
  const read = () => hub?.getSnapshot() ?? noResume;
  const state = useSyncExternalStore(hub ? hub.subscribe : noSubscription, read, read);
  if (!hub || state.pending?.id !== offer.id) return null;
  const line = resumeLine(state.pending, new Date(), locale);
  return <div className="timeline-resume" role="group" aria-label={t("Automatic resume")}>
    <AppIcon name="reminder" size={14} />
    <span className="timeline-resume-when">{t(line.key, line.values)}</span>
    <Button size="small" variant="outlined" intent="primary" loading={state.acting === "resume"} disabled={!!state.acting}
      onClick={() => void hub.act("resume", offer.id)}>{t("Resume now")}</Button>
    <Button size="small" variant="minimal" loading={state.acting === "cancel"} disabled={!!state.acting}
      onClick={() => void hub.act("cancel", offer.id)}>{t("Cancel")}</Button>
    {state.refused && <span role="alert" className="timeline-resume-refused">{t("The resume was not changed ({status}).", { status: state.refused })}</span>}
  </div>;
}
