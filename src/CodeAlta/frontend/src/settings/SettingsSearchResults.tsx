import { Fragment, type ReactNode } from "react";
import { Button, NonIdealState } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { translate } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { settingText, type SettingEntry } from "./settingsCatalog";
import { settingsPageOf } from "./settingsPages";
import { foldText, groupResults, queryWords, settingPagePath, type SettingsSearchResult } from "./settingsSearch";
import { editableInPlace, SettingControl } from "./SettingControl";
import type { SettingValueSource } from "./settingValues";

/** Marks the words of a query in a text, as the search found them (case and accents aside). */
export function highlight(text: string, words: readonly string[]): ReactNode {
  if (!words.length) return text;
  const folded = foldText(text);
  // Folding keeps the length of most texts; where it does not, the text is shown without marks.
  if (folded.length !== text.length) return text;
  const marks: [number, number][] = [];
  for (const word of words) {
    let at = folded.indexOf(word);
    while (at >= 0) { marks.push([at, at + word.length]); at = folded.indexOf(word, at + word.length); }
  }
  if (!marks.length) return text;
  marks.sort((a, b) => a[0] - b[0]);
  const parts: ReactNode[] = [];
  let cursor = 0;
  for (const [start, end] of marks) {
    if (end <= cursor) continue;
    const from = Math.max(start, cursor);
    if (from > cursor) parts.push(text.slice(cursor, from));
    parts.push(<mark key={from}>{text.slice(from, end)}</mark>);
    cursor = end;
  }
  if (cursor < text.length) parts.push(text.slice(cursor));
  return parts;
}

/**
 * The results of the search of Settings: the matching settings of every page, grouped by page and by the part of the
 * page they are in, each with its control where the search can change it, and a way to its page; then the pages found
 * by their name. It reads like the pages it comes from, so a setting is changed here without opening its page.
 */
export function SettingsSearchResults({ query, result, entries, source, revision, onOpen }: {
  query: string; result: SettingsSearchResult; entries: readonly SettingEntry[]; source: SettingValueSource; revision: unknown;
  /** Opens a page of Settings, and makes a setting of it glow. */
  onOpen: (page: string, setting: string | null) => void;
}) {
  const { t, locale } = useShellLanguage();
  const words = queryWords(query);
  const groups = groupResults(result, entries);
  const shownPages = new Set(groups.map(group => group.page));
  const pages = result.pages.filter(match => !shownPages.has(match.page.id));
  const count = result.settings.length + pages.length;
  if (count === 0) {
    return <main className="configuration-page settings-search-results" aria-label={t("Search settings")}>
      <NonIdealState icon={<AppIcon name="search" size={36} />} title={t("No setting matches “{query}”.", { query: query.trim() })} />
    </main>;
  }
  return <main className="configuration-page settings-search-results" aria-label={t("Search settings")}>
    <header className="page-heading"><span className="eyebrow">{t("Settings")}</span>
      <h1 role="status">{count === 1 ? t("1 result for “{query}”", { query: query.trim() }) : t("{count} results for “{query}”", { count, query: query.trim() })}</h1></header>
    {groups.map(group => {
      const first = group.settings[0];
      const page = settingsPageOf(group.page);
      return <section key={group.page} className="settings-search-group" aria-label={settingPagePath(locale, first)}>
        <header className="settings-search-group-heading">
          {page && <AppIcon name={page.icon} size={15} />}
          <h2>{highlight(settingPagePath(locale, first), words)}</h2>
          <Button variant="minimal" size="small" endIcon={<AppIcon name="chevronRight" size={14} />} onClick={() => onOpen(group.page, null)}>{t("Open page")}</Button>
        </header>
        {group.settings.map((entry, index) => {
          const section = entry.section ? settingText(locale, entry.section, entry.builtIn) : null;
          const previous = index > 0 ? group.settings[index - 1].section : undefined;
          const label = settingText(locale, entry.label, entry.builtIn);
          return <Fragment key={entry.id}>
            {section && entry.section !== previous && <h3 className="settings-search-section">{highlight(section, words)}</h3>}
            <div className="settings-field settings-search-row" data-setting-result={entry.id}>
              <span className="settings-field-label">{highlight(label, words)}</span>
              <div className="settings-field-control">
                {editableInPlace(entry)
                  ? <SettingControl entry={entry} source={source} revision={revision} label={label} />
                  : <Button size="small" variant="outlined" onClick={() => onOpen(entry.page, entry.id)}
                    title={entry.writable ? undefined : t("Change it on its page")}>{t("Open")}</Button>}
              </div>
              {entry.help && <p className="settings-field-help">{highlight(settingText(locale, entry.help, entry.builtIn), words)}</p>}
              {entry.applies === "nextStart" && <p className="settings-field-help">{t("Applies at next startup")}</p>}
            </div>
          </Fragment>;
        })}
      </section>;
    })}
    {pages.length > 0 && <section className="settings-search-group settings-search-pages" aria-label={t("Settings pages")}>
      <header className="settings-search-group-heading"><h2>{t("Settings pages")}</h2></header>
      {pages.map(match => <button key={match.page.id} type="button" className="settings-search-page" onClick={() => onOpen(match.page.id, null)}>
        <AppIcon name={match.page.icon} size={16} />
        <span>{highlight(`${translate(locale, match.page.group)} › ${translate(locale, match.page.label)}`, words)}</span>
        <AppIcon name="chevronRight" size={14} />
      </button>)}
    </section>}
  </main>;
}
