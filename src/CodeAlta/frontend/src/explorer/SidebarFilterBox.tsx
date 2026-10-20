import { InputGroup } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/**
 * The filter at the top of the sidebar: what is typed narrows the sessions, the projects and the Claude Code sessions
 * started elsewhere to those whose title has every word. Escape empties it.
 */
export function SidebarFilterBox({ value, onChange, empty = false }: {
  value: string;
  onChange: (value: string) => void;
  /** Whether nothing matches what is typed: the box says so. */
  empty?: boolean;
}) {
  const { t } = useShellLanguage();
  return <div className="sidebar-filter">
    <InputGroup type="text" size="small" spellCheck={false} autoComplete="off" value={value} placeholder={t("Filter sessions")}
      aria-label={t("Filter the sessions and the projects by title")} leftIcon={<AppIcon name="search" size={13} className="bp6-icon" />}
      rightElement={value ? <button type="button" className="icon-button sidebar-filter-clear" aria-label={t("Clear the filter")} title={t("Clear the filter")}
        onClick={() => onChange("")}><AppIcon name="close" size={12} /></button> : undefined}
      onChange={event => onChange(event.target.value)}
      onKeyDown={event => {
        if (event.key !== "Escape" || event.nativeEvent.isComposing || !value) return;
        event.preventDefault(); event.stopPropagation();
        onChange("");
      }} />
    {empty && <p className="sidebar-filter-empty muted-text" role="status">{t("Nothing matches.")}</p>}
  </div>;
}
