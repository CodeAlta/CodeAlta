import { useEffect, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { EntryAddedPicture } from "./EntryAddedPicture";
import { frontLayer } from "./frontLayer";
import { useShellLanguage } from "./shellLanguage";
import { logoUrl } from "./windowChrome";

/**
 * Said once on macOS, the first time the installed tool becomes an application: where it is, how it is
 * started from now on, and how it gets into the Dock. The Dock has no way for an application to add itself,
 * so the picture shows the gesture and a button opens the folder with the application selected.
 */
export function EntryAddedDialog({ onShowInFinder, onClose }: { onShowInFinder: () => void; onClose: () => void }) {
  const { t } = useShellLanguage();
  // It is shown once the settings of a first start are closed, in the same update that takes their window
  // away: the window in front is looked for after that update, or the dialog would go away with it.
  const [layer, setLayer] = useState<{ front: HTMLElement | undefined } | null>(null);
  useEffect(() => setLayer({ front: frontLayer() }), []);
  if (!layer) return null;
  return <Dialog isOpen className="entry-added-dialog" title={t("CodeAlta is in your Applications folder")} portalContainer={layer.front} onClose={onClose}>
    <DialogBody>
      <EntryAddedPicture logo={logoUrl} label={t("CodeAlta dragged from the Applications folder to the Dock")} folder={t("Applications")} />
      <p>{t("Open CodeAlta like any other application: from Launchpad, Spotlight or the Applications folder of your home folder. No terminal is needed.")}</p>
      <p>{t("To keep it at hand, drag CodeAlta from that folder to the Dock.")}</p>
    </DialogBody>
    <DialogFooter actions={<>
      <Button onClick={onClose}>{t("Close")}</Button>
      <Button intent="primary" autoFocus icon={<AppIcon name="folder" size={15} />} onClick={onShowInFinder}>{t("Reveal in Finder")}</Button>
    </>} />
  </Dialog>;
}
