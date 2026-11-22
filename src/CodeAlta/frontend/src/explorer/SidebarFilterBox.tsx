import { InputGroup } from "@blueprintjs/core";
import { useEffect, useState } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/**
 * The sidebar filter keeps edits here until typing pauses. Escape or the clear button empties it immediately.
 */
export function SidebarFilterBox({ applied, onApply, empty = false }: {
  applied: string;
  onApply: (value: string) => void;
  /** Whether nothing matches the applied filter. */
  empty?: boolean;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState(applied);
  useEffect(() => {
    if (draft === applied) return;
    if (!draft) { onApply(""); return; }
    const timer = setTimeout(() => onApply(draft), 200);
    return () => clearTimeout(timer);
  }, [draft, applied, onApply]);

  return <div className="sidebar-filter">
    <InputGroup type="text" size="small" spellCheck={false} autoComplete="off" value={draft} placeholder={t("Filter sessions")}
      aria-label={t("Filter the sessions and the projects by title")} leftIcon={<AppIcon name="search" size={13} className="bp6-icon" />}
      rightElement={draft ? <button type="button" className="icon-button sidebar-filter-clear" aria-label={t("Clear the filter")} title={t("Clear the filter")}
        onClick={() => setDraft("")}><AppIcon name="close" size={12} /></button> : undefined}
      onChange={event => setDraft(event.target.value)}
      onKeyDown={event => {
        if (event.key !== "Escape" || event.nativeEvent.isComposing || !draft) return;
        event.preventDefault(); event.stopPropagation();
        setDraft("");
      }} />
    {empty && draft === applied && <p className="sidebar-filter-empty muted-text" role="status">{t("Nothing matches.")}</p>}
  </div>;
}
