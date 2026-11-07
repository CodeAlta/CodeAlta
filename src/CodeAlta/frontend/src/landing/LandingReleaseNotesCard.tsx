import { useEffect, useState } from "react";
import { Button } from "@blueprintjs/core";
import { releaseNotes } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { settingsNavigation } from "../settingsNavigation";
import { releaseNotesPage, sameVersion, shownVersion } from "../releaseNotes/releaseNotes";

type Glance = Readonly<{ current: string; latest: string | null; isLatest: boolean | null }> | "failed";

/** What the card reads; the window's service unless a test brings its own. */
export type LandingReleaseNotesApi = Pick<typeof releaseNotes, "list">;

// One object for the life of the window: a new one at each render would read the releases again at each render.
const windowApi: LandingReleaseNotesApi = releaseNotes;

/**
 * What's new on the welcome page: the running version, whether it is the latest one or a newer one is out, and the way
 * to the release notes.
 */
export function LandingReleaseNotesCard({ epoch, visible, api = windowApi }: { epoch: string | null; visible: boolean; api?: LandingReleaseNotesApi }) {
  const { t } = useShellLanguage();
  const [glance, setGlance] = useState<Glance | null>(null);
  useEffect(() => {
    if (!epoch || !visible) return;
    const abort = new AbortController();
    void api.list({ refresh: false }, { signal: abort.signal, timeoutMilliseconds: 45_000 }).then(value => {
      if (!abort.signal.aborted) setGlance(value.status === "ok" ? { current: value.currentVersion, latest: value.latestVersion, isLatest: value.isLatest } : "failed");
    }, () => { if (!abort.signal.aborted) setGlance("failed"); });
    return () => abort.abort();
  }, [api, epoch, visible]);
  const shown = glance === "failed" ? null : glance;
  // A pre-release built after the latest release (a build of the repository) is ahead of it, not the latest release.
  const ahead = shown?.isLatest === true && shownVersion(shown.current).includes("-") && !!shown.latest && !sameVersion(shownVersion(shown.current), shown.latest);
  return <section className="landing-card landing-release-notes" data-list="release-notes" aria-label={t("What's new in CodeAlta")}>
    <header className="landing-card-header"><AppIcon name="releaseNotes" size={16} className="landing-card-icon landing-release-notes-icon" aria-hidden="true" /><h2>{t("What's new in CodeAlta")}</h2></header>
    <div className="landing-release-notes-glance">
      {shown && <strong title={shown.current}>{t("Version {version}", { version: shownVersion(shown.current) })}</strong>}
      {ahead && <span className="landing-release-notes-state" data-state="development"><AppIcon name="code" size={14} />{t("A pre-release, built after {latest}.", { latest: shown!.latest! })}</span>}
      {shown?.isLatest === true && !ahead && <span className="landing-release-notes-state" data-state="latest"><AppIcon name="done" size={14} />{t("You run the latest version.")}</span>}
      {shown?.isLatest === false && shown.latest && <span className="landing-release-notes-state" data-state="behind">
        <AppIcon name="whatsNew" size={14} />{t("A new version is out: {version}.", { version: shown.latest })}</span>}
    </div>
    <Button className="landing-release-notes-open" icon={<AppIcon name="releaseNotes" size={15} />} intent={shown?.isLatest === false ? "primary" : "none"}
      onClick={() => settingsNavigation.open(releaseNotesPage)}>{t("Read the release notes")}</Button>
  </section>;
}
