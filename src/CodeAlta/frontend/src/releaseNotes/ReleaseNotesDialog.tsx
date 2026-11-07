import { useContext, useEffect, useMemo, useState } from "react";
import { Button, Checkbox, MenuItem, NonIdealState, Tag } from "@blueprintjs/core";
import { Select, type ItemRenderer } from "@blueprintjs/select";
import "@blueprintjs/select/lib/css/blueprint-select.css";
import { releaseNotes, type ReleaseNotesEntry, type ReleaseNotesListResponse, type ReleaseNotesReadResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { MarkdownContent, MarkdownLinksContext } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import { createReleaseNotesImages, matchesRelease, sameVersion, shownVersion } from "./releaseNotes";
import "./releaseNotes.css";

/** What the dialog reads: the window's service unless a test brings its own. */
export type ReleaseNotesApi = Readonly<{ list: typeof releaseNotes.list; read: typeof releaseNotes.read; picture: typeof releaseNotes.picture }>;

/** A newer version this computer can update to, as the update check found it. */
export type ReleaseNotesUpdate = Readonly<{ version: string; canInstall: boolean }>;

/**
 * The release notes of CodeAlta, as GitHub publishes them, in a window about as wide as GitHub shows them: the running
 * version and whether a newer one is out, a search of the versions, and the notes of the chosen one. It opens at startup
 * once after an update, unless the person ticked "Don't show again on startup".
 */
export function ReleaseNotesDialog({ version: initial = null, api = releaseNotes, update = null, onInstallUpdate, hideOnStartup, onHideOnStartup, onClose }: {
  /** The version shown first; null for the running one, else the latest. */
  version?: string | null; api?: ReleaseNotesApi;
  update?: ReleaseNotesUpdate | null; onInstallUpdate?: () => void;
  hideOnStartup: boolean; onHideOnStartup: (hide: boolean) => void; onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const openLink = useContext(MarkdownLinksContext);
  const [list, setList] = useState<ReleaseNotesListResponse | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [notes, setNotes] = useState<ReleaseNotesReadResponse | null>(null);
  const [generation, setGeneration] = useState(0);
  const date = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "long" }), [locale]);
  const shortDate = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }), [locale]);
  // The pictures of the notes, read by the host and kept while the dialog is open.
  const images = useMemo(() => createReleaseNotesImages(api.picture), [api]);

  useEffect(() => {
    const abort = new AbortController();
    setList(null);
    void api.list({ refresh: generation > 0 }, { signal: abort.signal, timeoutMilliseconds: 45_000 }).then(value => {
      if (abort.signal.aborted) return;
      setList(value);
      // The version asked for, else the running one when it is published, else the latest one.
      const find = (version: string | null | undefined) => value.releases.find(release => sameVersion(release.version, version))?.version;
      setSelected(previous => previous ?? find(initial) ?? find(value.currentVersion) ?? find(value.latestVersion) ?? value.releases[0]?.version ?? null);
    }, () => { if (!abort.signal.aborted) setList({ status: "failed", currentVersion: "", latestVersion: null, isLatest: null, releases: [], message: null }); });
    return () => abort.abort();
  }, [api, initial, generation]);

  useEffect(() => {
    if (!selected) return;
    const abort = new AbortController();
    setNotes(null);
    void api.read({ version: selected }, { signal: abort.signal, timeoutMilliseconds: 45_000 }).then(value => { if (!abort.signal.aborted) setNotes(value); },
      () => { if (!abort.signal.aborted) setNotes({ status: "failed", release: null, url: null, body: null, message: null }); });
    return () => abort.abort();
  }, [api, selected, generation]);

  const releases = list?.releases ?? [];
  const current = list?.currentVersion ?? "";
  const latest = list?.latestVersion ?? null;
  const chosen = releases.find(release => release.version === selected) ?? null;
  const published = (release: ReleaseNotesEntry | null) => release?.publishedAt ? new Date(release.publishedAt) : null;
  const renderItem: ItemRenderer<ReleaseNotesEntry> = (release, { handleClick, handleFocus, modifiers, ref }) => {
    if (!modifiers.matchesPredicate) return null;
    const at = published(release);
    return <MenuItem key={release.version} ref={ref} active={modifiers.active} disabled={modifiers.disabled} selected={release.version === selected}
      roleStructure="listoption" onClick={handleClick} onFocus={handleFocus}
      text={<span className="release-notes-option"><strong>{release.version}</strong>
        {sameVersion(release.version, current) && <Tag minimal round intent="primary">{t("Yours")}</Tag>}
        {sameVersion(release.version, latest) && <Tag minimal round intent="success">{t("Latest")}</Tag>}
        {release.prerelease && <Tag minimal round intent="warning">{t("Pre-release")}</Tag>}</span>}
      label={at ? shortDate.format(at) : undefined} />;
  };

  return <AppWindow storageKey="codealta.desktop.window.release-notes.v1" className="release-notes-dialog" titleId="release-notes-title" title={t("Release notes")}
    closeLabel={t("Close")} onClose={onClose} minimumSize={{ width: 520, height: 420 }}
    // About the width GitHub gives the notes of a release, and most of the height of the window.
    preferredSize={viewport => ({ width: Math.min(1040, viewport.width), height: viewport.height * 0.9 })}>
    <div className="release-notes">
      <header className="release-notes-header">
        <Select<ReleaseNotesEntry> items={[...releases]} itemPredicate={(query, release) => matchesRelease(query, release)} itemRenderer={renderItem}
          onItemSelect={release => setSelected(release.version)} disabled={releases.length === 0} resetOnClose
          popoverProps={{ placement: "bottom-start", minimal: true, popoverClassName: "release-notes-versions" }}
          inputProps={{ placeholder: t("Search a version…"), "aria-label": t("Search a version…") }}
          noResults={<MenuItem disabled text={t("No version matches.")} roleStructure="listoption" />}>
          <Button className="release-notes-version" icon={<AppIcon name="tag" size={15} />} endIcon={<AppIcon name="chevronDown" size={14} />}
            aria-label={t("Version")} text={chosen ? chosen.version : t("Version")} disabled={releases.length === 0} />
        </Select>
        <RunningVersion list={list} update={update} onInstallUpdate={onInstallUpdate} onShowLatest={() => latest && setSelected(latest)} />
      </header>
      <article className="release-notes-article" aria-busy={!notes || undefined}>
        {list?.status === "failed" ? <NonIdealState icon={<AppIcon name="warning" size={34} />} title={t("The release notes could not be read from GitHub.")}
          description={list.message ?? undefined}
          action={<Button icon={<AppIcon name="refresh" size={15} />} onClick={() => setGeneration(value => value + 1)}>{t("Try again")}</Button>} />
          : !notes ? <div className="release-notes-loading"><ActivitySpinner size={24} /><span>{t("Reading the release notes…")}</span></div>
          : notes.status !== "ok" || !notes.release ? <NonIdealState icon={<AppIcon name="warning" size={34} />} title={t("The release notes of this version could not be read.")}
            description={notes.message ?? undefined}
            action={<Button icon={<AppIcon name="refresh" size={15} />} onClick={() => setGeneration(value => value + 1)}>{t("Try again")}</Button>} />
          : <>
            <div className="release-notes-title">
              <h1>{notes.release.name}</h1>
              <div className="release-notes-meta">
                {published(notes.release) && <span><AppIcon name="calendar" size={14} />{date.format(published(notes.release)!)}</span>}
                {sameVersion(notes.release.version, latest) && <Tag round intent="success">{t("Latest")}</Tag>}
                {notes.release.prerelease && <Tag round intent="warning">{t("Pre-release")}</Tag>}
                {sameVersion(notes.release.version, current) && <Tag round minimal intent="primary">{t("The version you run")}</Tag>}
                {notes.url && openLink && <Button className="release-notes-github" variant="minimal" size="small" icon={<AppIcon name="openExternal" size={14} />}
                  text={t("View on GitHub")} onClick={() => openLink(notes.url!, null)} />}
              </div>
            </div>
            {notes.body?.trim() ? <MarkdownContent source={notes.body} document webImages={images} />
              : <p className="muted-text">{t("This release has no notes.")}</p>}
          </>}
      </article>
      <footer className="release-notes-footer">
        <Checkbox checked={hideOnStartup} label={t("Don't show again on startup")} onChange={event => onHideOnStartup(event.currentTarget.checked)} />
        <Button intent="primary" text={t("Close")} onClick={onClose} />
      </footer>
    </div>
  </AppWindow>;
}

/** The running version, and whether it is the latest one or a newer one is out, with the way to it. */
function RunningVersion({ list, update, onInstallUpdate, onShowLatest }: {
  list: ReleaseNotesListResponse | null; update: ReleaseNotesUpdate | null; onInstallUpdate?: () => void; onShowLatest: () => void;
}) {
  const { t } = useShellLanguage();
  const version = shownVersion(list?.currentVersion ?? "");
  if (!list || list.status !== "ok") return <span className="release-notes-running muted-text">{version ? t("You run CodeAlta {version}", { version }) : null}</span>;
  // A pre-release built after the latest release (a build of the repository) is ahead of it, not the latest release.
  const ahead = list.isLatest === true && version.includes("-") && !!list.latestVersion && !sameVersion(version, list.latestVersion);
  if (ahead) return <span className="release-notes-running" data-state="development" title={list.currentVersion}><AppIcon name="code" size={16} />
    <span>{t("You run a pre-release of CodeAlta, {version}, built after {latest}.", { version, latest: list.latestVersion! })}</span></span>;
  if (list.isLatest === true) return <span className="release-notes-running" data-state="latest">
    <AppIcon name="done" size={16} /><span>{t("You run CodeAlta {version}, the latest version.", { version })}</span></span>;
  if (list.isLatest === false && list.latestVersion) return <span className="release-notes-running" data-state="behind">
    <AppIcon name="whatsNew" size={16} /><span>{t("CodeAlta {latest} is out: you run {version}.", { latest: list.latestVersion, version })}</span>
    <Button size="small" variant="minimal" text={t("What's new")} onClick={onShowLatest} />
    {update?.canInstall && onInstallUpdate && <Button size="small" intent="primary" icon={<AppIcon name="download" size={14} />}
      text={t("Update to {version}", { version: update.version })} onClick={onInstallUpdate} />}
  </span>;
  return <span className="release-notes-running" data-state="development" title={list.currentVersion}><AppIcon name="code" size={16} />
    <span>{t("You run a development build of CodeAlta, {version}.", { version })}</span></span>;
}
