import { useRef, useState, type KeyboardEvent } from "react";
import { Button, Checkbox, Dialog, DialogBody, DialogFooter } from "@blueprintjs/core";
import { closeQuestion, nextChoice } from "./desktopShell";
import { frontLayer } from "./frontLayer";
import { useShellLanguage } from "./shellLanguage";

/**
 * Asks, when the window is closed, whether CodeAlta keeps running behind its icon or exits, until an answer is
 * remembered. Keep running is the default: it has the focus, and Enter chooses it from the check box as well.
 * The arrow keys move between the buttons; Escape leaves the window open.
 */
export function CloseWindowDialog({ platform, onKeepRunning, onExit, onCancel }: {
  platform: string;
  /** The answers; each says whether it is to be remembered. */
  onKeepRunning: (remember: boolean) => void; onExit: (remember: boolean) => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const [remember, setRemember] = useState(false);
  const layer = useRef(frontLayer());
  // What had the focus gets it back when the window stays as it was; an exit goes on with its own questions.
  const origin = useRef(document.activeElement instanceof HTMLElement ? document.activeElement : null);
  const restoreFocus = () => requestAnimationFrame(() => {
    const front = frontLayer();
    if (origin.current?.isConnected && (!front || front.contains(origin.current))) origin.current.focus();
  });
  const cancel = () => { onCancel(); restoreFocus(); };
  const keepRunning = () => { onKeepRunning(remember); restoreFocus(); };
  function choiceKey(event: KeyboardEvent<HTMLElement>) {
    const choices = Array.from(event.currentTarget.parentElement?.querySelectorAll<HTMLElement>("button") ?? []);
    const next = nextChoice(choices.indexOf(event.currentTarget), event.key, choices.length);
    if (next < 0) return;
    event.preventDefault();
    choices[next].focus();
  }
  return <Dialog isOpen className="close-window-dialog" title={t("Keep CodeAlta running?")} isCloseButtonShown={false} canOutsideClickClose={false}
    portalContainer={layer.current} onClose={cancel}>
    <DialogBody>
      <p>{t(closeQuestion(platform))}</p>
      <Checkbox checked={remember} label={t("Remember my choice")} onChange={event => setRemember(event.currentTarget.checked)}
        onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); keepRunning(); } }} />
    </DialogBody>
    <DialogFooter actions={<>
      <Button onKeyDown={choiceKey} onClick={cancel}>{t("Cancel")}</Button>
      <Button onKeyDown={choiceKey} onClick={() => onExit(remember)}>{t("Exit CodeAlta")}</Button>
      <Button intent="primary" autoFocus onKeyDown={choiceKey} onClick={keepRunning}>{t("Keep running")}</Button>
    </>} />
  </Dialog>;
}
