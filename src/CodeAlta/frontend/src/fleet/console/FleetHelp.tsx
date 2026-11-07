import { useState } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { useSettingsHelp } from "../../settingsHelp";

/**
 * The help of a part of the Fleet console: a small question mark that says in a sentence or two what the part is for,
 * and opens its page of the user guide; the page itself stays short. The guide opens in the place of Settings: while a
 * page has edits that are not saved, it says so first, and the person keeps editing or opens the guide anyway.
 */
export function FleetHelp({ text, page, anchor = null, label }: { text: MessageKey; page: string; anchor?: string | null; label?: string }) {
  const { t } = useShellLanguage();
  const help = useSettingsHelp();
  const [warning, setWarning] = useState(false);
  return <PopoverNext placement="bottom-start" onClosed={() => setWarning(false)} content={<div className="fleet-help">
    <p>{t(text)}</p>
    {warning ? <>
      <p className="fleet-help-warning"><AppIcon name="warning" size={14} />{t("Your changes on this page are not saved: opening the guide closes Settings and loses them.")}</p>
      <div className="fleet-help-actions">
        <Button className="bp6-popover-dismiss" size="small" intent="primary" text={t("Keep editing")} />
        <Button className="bp6-popover-dismiss" size="small" variant="minimal" intent="danger" text={t("Open the guide anyway")} onClick={() => help?.openGuide(page, anchor)} />
      </div>
    </> : help && <Button size="small" variant="minimal" icon={<AppIcon name="documentation" size={14} />} text={t("Open the guide")}
      onClick={() => { if (help.unsaved()) setWarning(true); else help.openGuide(page, anchor); }} />}
  </div>}>
    <Button className="fleet-help-button" variant="minimal" size="small" icon={<AppIcon name="help" size={15} />} aria-label={label ?? t("Help")} title={t("Help")} />
  </PopoverNext>;
}
