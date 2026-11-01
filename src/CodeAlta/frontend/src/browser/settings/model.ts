// What the settings page of the Browser plugin reads and writes: the answers of its calls (`browser.settings.get`, `set`,
// `browser.profile.open`, `browser.profile.delete`), and what a person types. Nothing here touches the page.

/** A browser of this computer, with the evidence of its use. */
export type BrowserFound = Readonly<{
  id: string; name: string; version: string | null; engine: "chromium" | "firefox" | "safari"; path: string;
  isDefault: boolean;
  /** When it was last used, as an ISO date; null when there is no evidence. */
  lastUsed: string | null;
  /** Whether its use can be read: Safari counts by its default status only. */
  measurable: boolean;
  /** Whether a session can browse in the user's own profile of it. */
  supportsUserProfile: boolean;
}>;

/** The browser a session gets, and why. */
export type BrowserChoice = Readonly<{ id: string; name: string; source: string; reason: string }>;

/** What the plugin says of its settings. */
export type BrowserSettingsView = Readonly<{
  browsers: readonly BrowserFound[];
  /** `auto`, the id of a browser found, or the path of a program. */
  browser: string;
  /** The engine of a program: `chromium` or `firefox`; null otherwise. */
  engine: "chromium" | "firefox" | null;
  /** `isolated`, `persistent`, `persistent:<name>` or `user`. */
  profile: string;
  headless: boolean;
  evaluateScript: boolean;
  user: Readonly<{ enabled: boolean; allowedSites: readonly string[]; blockedSites: readonly string[] }>;
  /** The browser a session gets with these settings; null when none fits. */
  choice: BrowserChoice | null;
  /** The browser Automatic picks. */
  automatic: BrowserChoice | null;
  /** Why no browser fits; null when one does. */
  refusal: string | null;
  persistentProfiles: readonly string[];
  /** Why a change was refused; null otherwise. */
  error: string | null;
  /** What a change did, such as the browser opened on a profile; null otherwise. */
  notice: string | null;
}>;

/** A change of the settings: each value that is set replaces the setting. */
export type BrowserSettingsChange = Partial<Readonly<{
  browser: string; engine: "chromium" | "firefox"; profile: string; headless: boolean; evaluateScript: boolean;
  userProfileEnabled: boolean; allowedSites: readonly string[]; blockedSites: readonly string[];
}>>;

const text = (value: unknown, limit: number): string | null => typeof value === "string" && value.length <= limit ? value : null;
const texts = (value: unknown, limit: number): string[] | null =>
  Array.isArray(value) && value.length <= 1000 && value.every(item => text(item, limit) !== null) ? [...value] : null;
const engines = ["chromium", "firefox", "safari"] as const;

function readChoice(value: unknown): BrowserChoice | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  const id = text(record.id, 128), name = text(record.name, 256), source = text(record.source, 32), reason = text(record.reason, 600);
  return id && name && source && reason !== null ? { id, name, source, reason } : null;
}

function readBrowser(value: unknown): BrowserFound | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  const id = text(record.id, 128), name = text(record.name, 256), path = text(record.path, 2048);
  const engine = engines.find(item => item === record.engine);
  if (!id || !name || !path || !engine || typeof record.isDefault !== "boolean" || typeof record.measurable !== "boolean" || typeof record.supportsUserProfile !== "boolean") return null;
  const lastUsed = text(record.lastUsed, 64);
  return {
    id, name, version: text(record.version, 64) || null, engine, path, isDefault: record.isDefault,
    lastUsed: lastUsed && !Number.isNaN(Date.parse(lastUsed)) ? lastUsed : null, measurable: record.measurable, supportsUserProfile: record.supportsUserProfile,
  };
}

/** Reads an answer of the plugin; null when it is not one. */
export function readBrowserSettings(value: unknown): BrowserSettingsView | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  const user = record.user && typeof record.user === "object" ? record.user as Record<string, unknown> : null;
  const browsers = Array.isArray(record.browsers) ? record.browsers.map(readBrowser) : null;
  const browser = text(record.browser, 2048), profile = text(record.profile, 128);
  const allowed = texts(user?.allowedSites, 253), blocked = texts(user?.blockedSites, 253), profiles = texts(record.persistentProfiles, 64);
  if (!browsers || browsers.some(item => item === null) || !browser || !profile || typeof record.headless !== "boolean" || typeof record.evaluateScript !== "boolean"
    || !user || typeof user.enabled !== "boolean" || !allowed || !blocked || !profiles) return null;
  return {
    browsers: browsers as BrowserFound[], browser, engine: record.engine === "chromium" || record.engine === "firefox" ? record.engine : null, profile,
    headless: record.headless, evaluateScript: record.evaluateScript, user: { enabled: user.enabled, allowedSites: allowed, blockedSites: blocked },
    choice: readChoice(record.choice), automatic: readChoice(record.automatic), refusal: text(record.refusal, 1000) || null, persistentProfiles: profiles,
    error: text(record.error, 1000) || null, notice: text(record.notice, 1000) || null,
  };
}

/** The kind of a profile setting: `isolated`, `persistent` or `user`. */
export const profileKind = (profile: string): "isolated" | "persistent" | "user" =>
  profile === "user" ? "user" : profile === "persistent" || profile.startsWith("persistent:") ? "persistent" : "isolated";

/** The name of the persistent profile of a profile setting, `default` for `persistent`. */
export const persistentName = (profile: string): string => profile.startsWith("persistent:") ? profile.slice("persistent:".length) : "default";

/** Whether a name can name a persistent profile: letters, digits, `-` and `_`, at most 64. */
export const isProfileName = (name: string): boolean => /^[A-Za-z0-9_-]{1,64}$/u.test(name);

/**
 * The site a person types, as the plugin keeps it: the host without `www.`, which also stands for its subdomains; null when it is no site.
 * An address gives its host.
 */
export function readSite(value: string): string | null {
  const typed = value.trim();
  if (!typed || /\s/u.test(typed)) return null;
  let host: string;
  try {
    // The address parser gives the host in lower case, with an international name in its ASCII form.
    const url = new URL(typed.includes("://") ? typed : `http://${typed}`);
    if (url.protocol !== "http:" && url.protocol !== "https:") return null;
    host = url.hostname;
  } catch { return null; }
  host = host.replace(/\.$/u, "");
  if (host.startsWith("www.") && host.length > 4) host = host.slice(4);
  return /^[a-z0-9.-]{1,253}$/u.test(host) && !host.startsWith(".") && !host.includes("..") ? host : null;
}

/** A list of sites with one more, kept once and in order. */
export const withSite = (sites: readonly string[], site: string): string[] => [...new Set([...sites, site])].sort();
