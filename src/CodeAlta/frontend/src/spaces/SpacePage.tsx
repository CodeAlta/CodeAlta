import { useEffect, useRef, useState, type CSSProperties } from "react";
import { Button, Callout, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { brandColor } from "../brands";
import { settingsFailure, type SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { SpaceAvailable } from "./SpaceAvailable";
import type { SpaceProject } from "./SpaceDialog";
import { SpaceGeneral, SpaceProjects } from "./SpaceGeneral";
import { SpaceIdentity } from "./SpaceIdentity";
import { SpaceNewSessions } from "./SpaceNewSessions";
import { SpaceTools } from "./SpaceTools";
import type { SpaceSectionProps } from "./spaceSection";
import { SpaceActivityMarks, SpaceIcon } from "./SpaceViews";
import type { Space, SpaceActivity } from "./spaces";
import type { SpaceOutcome, SpacesHub } from "./spacesHub";

/**
 * The page of one space in Settings > Spaces: what it is (General), its projects, and what it sets up for its
 * sessions, each section a component of its own. Back returns to the cards of every space.
 */
export function SpacePage({ hub, space, spaces, projects, chats, shown, activity, canEdit, onBack, onShow, onDeleted }: {
  hub: SpacesHub; space: Space; spaces: readonly Space[];
  /** Every project of the catalog. */
  projects: readonly SpaceProject[];
  /** How many chats the space has: deleting it moves them to the default space. */
  chats: number;
  /** Whether the window shows this space. */
  shown: boolean;
  activity: SpaceActivity | undefined;
  /** Whether the host lets spaces be changed. */
  canEdit: boolean;
  onBack: () => void; onShow: () => void; onDeleted: () => void;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  // Another space is another page: what was said of the last one is not said of it.
  useEffect(() => setNotice(null), [space.id]);

  async function run(change: () => Promise<SpaceOutcome>, said: boolean = false) {
    setBusy(true); setNotice(null);
    const outcome = await change();
    if (!alive.current) return outcome;
    setBusy(false);
    const failure = outcome.ok ? null : settingsFailure(outcome.status, outcome.message) ?? { key: "The operation did not complete." as const, intent: "danger" as const };
    setNotice(failure ?? (said ? { key: "Saved.", intent: "success" } : null));
    return outcome;
  }

  async function remove() {
    const outcome = await run(() => hub.remove(space.id));
    if (outcome.ok && alive.current) onDeleted();
  }

  const section: SpaceSectionProps = { space, spaces, hub, canEdit, busy, run };
  return <main className="configuration-page settings-editor space-page" aria-label={t("Space: {name}", { name: space.name })}
    style={{ "--space-color": brandColor(space.color) ?? "var(--line-strong)" } as CSSProperties}>
    <nav className="space-page-trail" aria-label={t("Spaces")}>
      <Button variant="minimal" size="small" className="space-page-back" icon={<AppIcon name="arrowLeft" size={14} />} text={t("Spaces")} onClick={onBack} />
    </nav>
    <header className="page-heading settings-editor-heading space-page-heading">
      <div className="space-page-title">
        <span className="space-page-icon"><SpaceIcon space={space} size={26} /></span>
        <div>
          <h1>{space.name}</h1>
          {space.description && <p>{space.description}</p>}
        </div>
      </div>
      <div className="settings-editor-actions">
        {busy && <ActivitySpinner size={14} />}
        <SpaceActivityMarks activity={activity} />
        <Button className="space-show" variant={shown ? "minimal" : "outlined"} intent={shown ? "primary" : undefined} disabled={shown}
          icon={<AppIcon name={shown ? "check" : "eye"} size={14} />} text={t(shown ? "Shown" : "Show")} onClick={onShow} />
        {!space.isDefault && <PopoverNext placement="bottom-end" content={<div className="provider-settings-confirm">
          <p>{chats === 0 ? t("Delete {name}? Its projects stay in the other spaces they are in.", { name: space.name })
            : chats === 1 ? t("Delete {name}? Its chat goes to the Default space, and its projects stay in their other spaces.", { name: space.name })
            : t("Delete {name}? Its {count} chats go to the Default space, and its projects stay in their other spaces.", { name: space.name, count: chats })}</p>
          <Button className="bp6-popover-dismiss" intent="danger" disabled={!canEdit || busy} onClick={() => void remove()}>{t("Delete")}</Button></div>}>
          <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={!canEdit || busy} text={t("Delete")} />
        </PopoverNext>}
      </div>
    </header>
    {notice && <Callout intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>
      {t(notice.key)}{notice.detail && <div className="config-editor-diagnostic">{notice.detail}</div>}</Callout>}
    <div className="space-sections">
      <SpaceGeneral {...section} />
      <SpaceProjects {...section} projects={projects} />
      <SpaceAvailable {...section} />
      <SpaceNewSessions {...section} />
      <SpaceTools {...section} />
      <SpaceIdentity {...section} />
    </div>
  </main>;
}
