import type { SpaceLinkBrowserItem } from "#neoastra";

/** Where the links of a space open, as its card edits it: an empty browser is the default one, an empty profile none. */
export type LinksSetting = Readonly<{ browser: string; profile: string }>;

/** What the card of the links shows for a setting, from the browsers of this computer. */
export type LinksChoice = Readonly<{
  /** The default browser of the system, when it is known. */
  defaultBrowser: SpaceLinkBrowserItem | null;
  /** The browser the links open in: the one chosen, else the default one; null when it is not known. */
  target: SpaceLinkBrowserItem | null;
  /** Whether the setting names a browser this computer does not have. */
  browserMissing: boolean;
  /** Whether a profile can be chosen for the browser the links open in. */
  profileSelectable: boolean;
  /** The profile of the browser the setting names, as the browser writes it; the setting when the browser has none of that name. */
  profile: string;
  /** Whether the setting names a profile the browser does not have. */
  profileMissing: boolean;
  /**
   * Whether the browser lets a profile be chosen but lists none: a browser always has one, so the system kept the list out
   * of reach (macOS privacy), and the profile is typed.
   */
  profilesUnreadable: boolean;
  /**
   * Whether the typed profile of a Chromium browser is not the folder of a profile (`Default`, `Profile 1`…): the host
   * never gives such a name, which would make the browser create a new profile, and the links open without one.
   */
  profileNotFolder: boolean;
}>;

/** Whether a text is the folder of a profile of a Chromium browser, as the host takes it. */
export function isChromiumProfileFolder(name: string): boolean {
  return name === "Default" || /^Profile \d{1,4}$/.test(name);
}

/**
 * The profile kept when the browser changes: a profile belongs to one browser, so it stays only when the new one has a profile
 * of that name (whatever its case); else the links of the new browser open without a profile, rather than with a name it
 * does not know (which Firefox answers with an error).
 */
export function profileForBrowser(profile: string, browser: string, browsers: readonly SpaceLinkBrowserItem[]): string {
  if (!profile) return "";
  const target = browser ? browsers.find(candidate => candidate.id.toLowerCase() === browser.toLowerCase()) : browsers.find(candidate => candidate.isDefault);
  return target?.profiles.find(name => name.toLowerCase() === profile.toLowerCase()) ?? "";
}

/** What the card shows for a setting. A profile is matched by its name whatever its case, as the host does. */
export function linksChoice(setting: LinksSetting, browsers: readonly SpaceLinkBrowserItem[]): LinksChoice {
  const defaultBrowser = browsers.find(browser => browser.isDefault) ?? null;
  const chosen = setting.browser ? browsers.find(browser => browser.id.toLowerCase() === setting.browser.toLowerCase()) ?? null : null;
  const target = setting.browser ? chosen : defaultBrowser;
  const browserMissing = !!setting.browser && !chosen;
  const matched = setting.profile ? target?.profiles.find(profile => profile.toLowerCase() === setting.profile.toLowerCase()) : undefined;
  // A profile the setting names stays to see while the browser is not known: it may be the one of another computer.
  const profileSelectable = target ? target.supportsProfiles : !!setting.profile;
  const profilesUnreadable = !!target?.supportsProfiles && target.profiles.length === 0;
  return {
    profilesUnreadable,
    profileNotFolder: profilesUnreadable && !target!.id.startsWith("firefox") && !!setting.profile && !isChromiumProfileFolder(setting.profile),
    defaultBrowser,
    target,
    browserMissing,
    profileSelectable,
    profile: matched ?? setting.profile,
    profileMissing: !!setting.profile && profileSelectable && !profilesUnreadable && matched === undefined,
  };
}
