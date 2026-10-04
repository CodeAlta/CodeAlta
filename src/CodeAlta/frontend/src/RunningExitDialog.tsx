import { Button, Dialog, DialogBody, DialogFooter } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { exitQuestion } from "./desktopShell";
import { useShellLanguage } from "./shellLanguage";

/** Asks before an exit that stops running sessions. Cancel is the default: Enter does not stop them. */
export function RunningExitDialog({ runningSessions, onExit, onCancel }: { runningSessions: number; onExit: () => void; onCancel: () => void }) {
  const { t } = useShellLanguage();
  const question = exitQuestion(runningSessions);
  return <Dialog isOpen className="running-exit-dialog" title={t("Exit CodeAlta?")} icon={<AppIcon name="error" size={18} />} isCloseButtonShown={false} onClose={onCancel}>
    <DialogBody>{t(question.key, question.parameters)}</DialogBody>
    <DialogFooter actions={<>
      <Button autoFocus onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="danger" onClick={onExit}>{t("Exit CodeAlta")}</Button>
    </>} />
  </Dialog>;
}
