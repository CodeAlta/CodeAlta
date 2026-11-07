import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { documentationNavigation } from "../../documentationNavigation";

/**
 * The help of a part of the Fleet console: a small question mark that says in a sentence or two what the part is for,
 * and opens its page of the user guide; the page itself stays short.
 */
export function FleetHelp({ text, page, anchor = null, label }: { text: MessageKey; page: string; anchor?: string | null; label?: string }) {
  const { t } = useShellLanguage();
  return <PopoverNext placement="bottom-start" content={<div className="fleet-help">
    <p>{t(text)}</p>
    <Button className="bp6-popover-dismiss" size="small" variant="minimal" icon={<AppIcon name="documentation" size={14} />} text={t("Open the guide")}
      onClick={() => documentationNavigation.open(page, anchor)} />
  </div>}>
    <Button className="fleet-help-button" variant="minimal" size="small" icon={<AppIcon name="help" size={15} />} aria-label={label ?? t("Help")} title={t("Help")} />
  </PopoverNext>;
}
