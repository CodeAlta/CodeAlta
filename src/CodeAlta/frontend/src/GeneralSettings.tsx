import type { ProjectSort } from "./projectRail";
import type { Theme } from "./windowPreferences";

export function GeneralSettings({ theme, setTheme, sort, setSort, desktopCollapsed, setDesktopCollapsed, notices }: {
  theme: Theme;
  setTheme: (value: Theme) => void;
  sort: ProjectSort;
  setSort: (value: ProjectSort) => void;
  desktopCollapsed: boolean;
  setDesktopCollapsed: (value: boolean) => void;
  notices: Partial<Record<"theme" | "sort" | "rail", string>>;
}) {
  return <section className="settings-card" aria-labelledby="general-settings-title"><div className="settings-icon">◐</div><div>
    <h2 id="general-settings-title">Appearance &amp; navigator</h2>
    <p>Local preferences for this window. Changes apply immediately.</p>
    <fieldset><legend>Theme</legend><div className="segmented">
      <button type="button" aria-pressed={theme === "dark"} onClick={() => setTheme("dark")}>Dark</button>
      <button type="button" aria-pressed={theme === "light"} onClick={() => setTheme("light")}>Light</button>
    </div></fieldset>
    {notices.theme && <p role="status" className="notice">{notices.theme}</p>}
    <label htmlFor="settings-project-sort">Sort projects</label>
    <select id="settings-project-sort" value={sort} onChange={event => setSort(event.target.value as ProjectSort)}>
      <option value="name">Name</option><option value="recent">Recent visible updates</option>
    </select>
    <p className="muted-text">Recent uses verified visible saved session updates, not the TUI last-active order. Missing or truncated evidence cannot establish recency; undated projects follow name order.</p>
    {notices.sort && <p role="status" className="notice">{notices.sort}</p>}
    <label className="settings-checkbox"><input type="checkbox" checked={desktopCollapsed} onChange={event => setDesktopCollapsed(event.target.checked)} />Collapse desktop project rail</label>
    <p className="muted-text">On narrow screens, Show projects temporarily reveals the rail without changing this desktop preference.</p>
    {notices.rail && <p role="status" className="notice">{notices.rail}</p>}
  </div></section>;
}
