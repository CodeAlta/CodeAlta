import { useEffect, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import type { AppUpdateResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { translate, type Locale, type MessageKey } from "./localization";

/** A newer version with the command that installs it; null for any other result of the check. */
export type AvailableUpdate = Readonly<{ version: string; command: string; releaseNotes: boolean }>;

export function availableUpdate(update: Pick<AppUpdateResponse, "status" | "latestVersion" | "command" | "releaseNotes"> | null | undefined): AvailableUpdate | null {
  return update?.status === "available" && update.latestVersion && update.command
    ? { version: update.latestVersion, command: update.command, releaseNotes: !!update.releaseNotes } : null;
}

/** What the About page says about updates; null when the check says nothing worth a line. */
export function updateStatus(update: Pick<AppUpdateResponse, "status" | "latestVersion"> | null | undefined): { key: MessageKey; parameters?: Readonly<Record<string, string>> } | null {
  if (!update) return { key: "Checking for updates…" };
  switch (update.status) {
    case "available": return { key: "Version {version} is available.", parameters: { version: update.latestVersion ?? "?" } };
    case "latest": return { key: "You are running the latest version." };
    case "failed": return { key: "The update check failed." };
    default: return null; // A build that is not a published version, or a package that is not published.
  }
}

/**
 * The update command with a button that copies it, and the way to the release notes. It is shown in a toast,
 * which lives outside the application's tree, so it is given its language.
 */
export function UpdateNotice({ update, locale, title = true, onOpenReleaseNotes }: {
  update: AvailableUpdate; locale: Locale; title?: boolean; onOpenReleaseNotes: () => void;
}) {
  const t = (key: MessageKey, parameters?: Readonly<Record<string, string>>) => translate(locale, key, parameters);
  const [copied, setCopied] = useState(false);
  const reset = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(reset.current), []);
  return <div className="update-notice">
    {title && <strong>{t("CodeAlta {version} is available.", { version: update.version })}</strong>}
    <span className="update-notice-command"><code>{update.command}</code>
      <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "checked" : "copy"} size={14} />}
        aria-label={t(copied ? "Copied" : "Copy update command")} title={t(copied ? "Copied" : "Copy update command")}
        onClick={() => { void navigator.clipboard?.writeText(update.command).then(() => { setCopied(true); window.clearTimeout(reset.current);
          reset.current = window.setTimeout(() => setCopied(false), 1600); }, () => { /* The command stays selectable. */ }); }} /></span>
    <span className="update-notice-hint">{t("Exit CodeAlta, then run this command in a terminal.")}</span>
    {update.releaseNotes && <Button className="update-notice-notes" size="small" variant="minimal" intent="primary" onClick={onOpenReleaseNotes}>{t("View release notes")}</Button>}
  </div>;
}
