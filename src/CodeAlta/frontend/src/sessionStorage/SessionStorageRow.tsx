import { Button } from "@blueprintjs/core";
import { useEffect, useState } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { SessionStorageHost } from "./sessionStorage";

/**
 * The row of the Session Info window that tells the folder a session is stored in on this computer, with a button
 * that copies it and one that opens it in the file manager of the system. Read once when the window opens; nothing
 * is shown for a session whose journal is not found.
 */
export function SessionStorageRow({ host, write = navigator.clipboard ? text => navigator.clipboard.writeText(text) : undefined }: {
  host: SessionStorageHost;
  write?: (text: string) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const [folder, setFolder] = useState<string | null>(null);
  const [canReveal, setCanReveal] = useState(false);
  const [feedback, setFeedback] = useState<"copied" | "copyFailed" | "revealFailed" | null>(null);
  useEffect(() => {
    let alive = true;
    setFolder(null);
    setFeedback(null);
    void host.read().then(value => { if (alive) { setFolder(value?.folder ?? null); setCanReveal(value?.canReveal === true); } });
    return () => { alive = false; };
  }, [host]);
  if (!folder) return null;
  return <div className="session-storage-row"><dt>{t("Session folder")}</dt><dd>
    <span>{folder}</span>
    <span className="session-storage-actions">
      <Button variant="minimal" size="small" icon={<AppIcon name={feedback === "copied" ? "check" : "copy"} size={14} />} disabled={!write}
        aria-label={t("Copy the path of the session folder")} title={t("Copy the path of the session folder")}
        onClick={() => void write?.(folder).then(() => setFeedback("copied"), () => setFeedback("copyFailed"))} />
      {canReveal && <Button variant="minimal" size="small" icon={<AppIcon name="open" size={14} />}
        aria-label={t("Show the session in the file manager")} title={t("Show the session in the file manager")}
        onClick={() => void host.reveal().then(shown => setFeedback(shown ? null : "revealFailed"))} />}
    </span>
    {feedback === "copyFailed" && <small role="alert">{t("Clipboard unavailable; nothing copied.")}</small>}
    {feedback === "revealFailed" && <small role="alert">{t("The file manager could not be opened.")}</small>}
  </dd></div>;
}
