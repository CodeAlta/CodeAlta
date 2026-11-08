import type { MarkdownImage, MarkdownImages } from "../markdownImages";

/** The page of Settings' requests that opens the release notes (`alta release-notes --show`): then `/<version>`. */
export const releaseNotesPage = "release-notes";

/** The version a page of the release notes names: null for the running one; undefined when the page is another one. */
export function readReleaseNotesPage(page: string): string | null | undefined {
  if (page === releaseNotesPage) return null;
  if (!page.startsWith(`${releaseNotesPage}/`)) return undefined;
  const version = page.slice(releaseNotesPage.length + 1).trim();
  return version && version.length <= 64 && !/\s/u.test(version) ? version : null;
}

/** What the window remembers of the release notes: whether they open at startup, and the version they last opened for. */
export type ReleaseNotesPreferences = Readonly<{ hideOnStartup: boolean; lastVersion: string | null }>;

export const releaseNotesStorageKey = "codealta.desktop.releaseNotes.v1";

/** Reads the preferences; anything malformed is the default (shown at startup, no version seen yet). */
export function readReleaseNotesPreferences(read: (key: string) => string | null): ReleaseNotesPreferences {
  try {
    const value = JSON.parse(read(releaseNotesStorageKey) ?? "null") as Partial<ReleaseNotesPreferences> | null;
    return { hideOnStartup: value?.hideOnStartup === true, lastVersion: typeof value?.lastVersion === "string" ? value.lastVersion : null };
  } catch { return { hideOnStartup: false, lastVersion: null }; }
}

export function writeReleaseNotesPreferences(write: (key: string, value: string) => void, value: ReleaseNotesPreferences): void {
  try { write(releaseNotesStorageKey, JSON.stringify(value)); } catch { /* The choice holds for this window. */ }
}

/** Whether a version is one GitHub publishes notes of: a number first (`4.1.0`), not a development build. */
export function isReleaseVersion(version: string | null | undefined): version is string {
  return !!version && /^v?\d+\.\d+/u.test(version.trim());
}

/**
 * Whether a version is a build of a commit after the last tag of the repository: MinVer names it `X.Y.Z-alpha.0.<commits>`,
 * which changes at each commit. Nobody installs it as a version of CodeAlta.
 */
export function isRepositoryBuild(version: string): boolean {
  return /-alpha\.0\.\d+(?:\+|$)/u.test(version.trim());
}

/**
 * At startup: the release notes open when the running version is another one than the version they last opened for
 * (CodeAlta was updated), unless the person chose not to see them. The first run records its version and opens nothing:
 * nothing changed for that person yet. A development build, a build of the repository and the developer instance
 * (`developer`, started with --dev to work on CodeAlta) open nothing, and leave the last version as it was.
 */
export function startupReleaseNotes(preferences: ReleaseNotesPreferences, currentVersion: string | null | undefined, developer = false): { show: boolean; next: ReleaseNotesPreferences } {
  if (developer || !isReleaseVersion(currentVersion) || isRepositoryBuild(currentVersion) || sameVersion(preferences.lastVersion, currentVersion)) {
    return { show: false, next: preferences };
  }
  return { show: preferences.lastVersion !== null && !preferences.hideOnStartup, next: { ...preferences, lastVersion: currentVersion } };
}

/** A version as the window shows it: without the build that follows a `+` (the commit of a build of the repository). */
export function shownVersion(version: string): string {
  return version.split("+")[0]!.trim();
}

/** Whether two versions are the same, with or without a leading `v`. */
export function sameVersion(left: string | null | undefined, right: string | null | undefined): boolean {
  const trim = (value: string) => value.trim().replace(/^v(?=\d)/iu, "").toLowerCase();
  return !!left && !!right && trim(left) === trim(right);
}

/** Whether a release matches what is typed in the search of the versions: its version or its title, in any case. */
export function matchesRelease(query: string, release: Readonly<{ version: string; name: string }>): boolean {
  const words = query.trim().toLowerCase().replace(/^v(?=\d)/u, "").split(/\s+/u).filter(Boolean);
  const text = `${release.version} ${release.name}`.toLowerCase();
  return words.every(word => text.includes(word));
}

// The pictures of release notes are published on GitHub and on the site of CodeAlta: only those are asked of the host.
const pictureHosts = ["github.com", "githubusercontent.com", "codealta.io"];
function publicPicture(address: string): boolean {
  try {
    const url = new URL(address);
    return url.protocol === "https:" && pictureHosts.some(host => url.hostname === host || url.hostname.endsWith(`.${host}`));
  } catch { return false; }
}

/** Reads a picture of release notes: the host fetches it, as the page loads nothing of the web itself. */
export type ReleaseNotesPictureRead = (request: { address: string }, options: { timeoutMilliseconds: number }) =>
  Promise<Readonly<{ status: string; mediaType: string | null; data: string | null }>>;

// What release notes draw: the pictures of the web, SVG included. An SVG drawn by an image runs no script and loads
// nothing, and these come from CodeAlta's own repository and site.
const pictureTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml"];

/**
 * Draws the pictures of release notes that GitHub or the site of CodeAlta serves, read by the host and kept while the
 * dialog lives; any other picture stays a link.
 */
export function createReleaseNotesImages(read: ReleaseNotesPictureRead): MarkdownImages {
  const kept = new Map<string, MarkdownImage | null>();
  const reading = new Map<string, Promise<MarkdownImage | null>>();
  async function load(address: string): Promise<MarkdownImage | null> {
    try {
      const reply = await read({ address }, { timeoutMilliseconds: 60_000 });
      const image = reply.status === "ok" && reply.mediaType && pictureTypes.includes(reply.mediaType) && reply.data && !/[^A-Za-z0-9+/=]/u.test(reply.data)
        ? { url: `data:${reply.mediaType};base64,${reply.data}`, path: address } : null;
      // A read that failed is asked again when the notes are shown again; what is no picture is not.
      if (image || reply.status !== "failed") kept.set(address, image);
      return image;
    } catch { return null; }
    finally { reading.delete(address); }
  }
  return {
    peek: address => !publicPicture(address) ? null : kept.has(address) ? kept.get(address)! : undefined,
    load: address => {
      if (!publicPicture(address)) return Promise.resolve(null);
      if (kept.has(address)) return Promise.resolve(kept.get(address)!);
      let pending = reading.get(address);
      if (!pending) { pending = load(address); reading.set(address, pending); }
      return pending;
    },
    reveal: () => { /* A picture of the web is in no folder. */ },
    platform: null,
  };
}
