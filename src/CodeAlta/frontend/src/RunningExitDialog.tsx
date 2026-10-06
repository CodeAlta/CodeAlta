import { useRef } from "react";
import { Button, Dialog, DialogBody, DialogFooter } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { exitQuestion } from "./desktopShell";
import { frontLayer } from "./frontLayer";
import { useShellLanguage } from "./shellLanguage";

/** Asks before an exit that stops running sessions or ends what terminals run. Cancel is the default: Enter stops nothing. */
export function RunningExitDialog({ runningSessions, busyTerminals = 0, onExit, onCancel }: { runningSessions: number; busyTerminals?: number; onExit: () => void; onCancel: () => void }) {
  const { t } = useShellLanguage();
  const questions = exitQuestion(runningSessions, busyTerminals);
  const layer = useRef(frontLayer());
  return <Dialog isOpen className="running-exit-dialog" title={t("Exit CodeAlta?")} icon={<AppIcon name="error" size={18} />} isCloseButtonShown={false}
    portalContainer={layer.current} onClose={onCancel}>
    <DialogBody>{questions.map(question => <p key={question.key}>{t(question.key, question.parameters)}</p>)}</DialogBody>
    <DialogFooter actions={<>
      <Button autoFocus onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="danger" onClick={onExit}>{t("Exit CodeAlta")}</Button>
    </>} />
  </Dialog>;
}
