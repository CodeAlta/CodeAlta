import { useRef } from "react";
import { Button, Dialog, DialogBody, DialogFooter, Spinner } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { frontLayer } from "./frontLayer";
import { useShellLanguage } from "./shellLanguage";
import type { NextRuntime } from "./UpdateNotice";

/**
 * The reminder of an exit while the .NET the next major version runs on is not installed (#209), once a run: Install and
 * exit installs it, then CodeAlta exits; Exit CodeAlta exits as before; Escape stays in CodeAlta. While it installs, the
 * dialog says so, and Exit CodeAlta still exits at once.
 */
export function NextRuntimeExitDialog({ runtime, onInstall, onExit, onCancel }: {
  runtime: NextRuntime; onInstall: () => void; onExit: () => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const layer = useRef(frontLayer());
  const version = { version: runtime.version };
  const installing = runtime.status === "installing";
  return <Dialog isOpen className="next-runtime-exit-dialog" title={t("Install .NET {version} before you exit?", version)} icon={<AppIcon name="download" size={18} />}
    isCloseButtonShown={false} portalContainer={layer.current} onClose={onCancel}>
    <DialogBody>
      {installing
        ? <p className="next-runtime-exit-progress"><Spinner size={16} /><span>{t("Installing .NET {version}…", version)} {t("CodeAlta exits once it is installed.")}</span></p>
        : <>
          {runtime.status === "failed" && <p className="next-runtime-exit-failure" role="alert"><strong>{t("The installation of .NET {version} failed.", version)}</strong>{runtime.detail ? ` ${runtime.detail}` : ""}</p>}
          <p>{t("The next major version of CodeAlta runs on .NET {version}. Install it now, and the update to that version starts at once.", version)}</p>
          {runtime.scope === "system" && <p>{t("The system asks for your permission.")}</p>}
        </>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button onClick={onExit}>{t("Exit CodeAlta")}</Button>
      {!installing && <Button autoFocus intent="primary" onClick={onInstall}>{t(runtime.status === "failed" ? "Try again" : "Install and exit")}</Button>}
    </>} />
  </Dialog>;
}
