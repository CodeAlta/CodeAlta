import type { ReactNode } from "react";
import { Button, Tag } from "@blueprintjs/core";
import type { SettingsFileLocation as HostLocation } from "#neoastra";
import { AppIcon } from "./AppIcon";
import type { MessageKey } from "./localization";
import { useShellLanguage } from "./shellLanguage";

/** What names a file or a folder of Settings to the host, which finds its path: the page never sends one. */
export type SettingsFileTarget = Readonly<{ kind: string; scope: string; id?: string | null; part?: string | null }>;

/** The label of the action that shows a file in the file manager of a system; null when no file manager can be opened. */
export const revealLabel = (platform: string | null | undefined): MessageKey | null => !platform ? null
  : platform === "windows" ? "Reveal in File Explorer" : platform === "macos" ? "Reveal in Finder" : "Open containing folder";

/**
 * One file or folder that a settings page reads: its path on one line, cut at its start when it is too long, and
 * the quiet buttons that open it in the code editor, copy its path and show it in the file manager.
 */
export function SettingsFileLocation({ path, label, missing = false, readOnly = false, platform, disabled = false, onOpen, onReveal, openLabel, copy }: {
  path: string;
  /** What the path is, before it: a scope, or the name of the file. */
  label?: ReactNode;
  /** The file or the folder is not on disk yet. */
  missing?: boolean;
  /** It is only read: the code editor shows it, and changes nothing. */
  readOnly?: boolean;
  /** The system, which names its file manager; null where a file cannot be shown. */
  platform?: string | null;
  disabled?: boolean;
  onOpen?: () => void;
  onReveal?: () => void;
  /** The accessible name of the button that opens it, when the path alone does not say what it is. */
  openLabel?: string;
  /** Copies the path; the clipboard of the window by default. */
  copy?: (path: string) => void;
}) {
  const { t } = useShellLanguage();
  const reveal = revealLabel(platform);
  const open = t(readOnly ? "Open in the code editor" : "Edit in the code editor");
  return <span className="settings-file-location" data-missing={missing ? "true" : undefined}>
    {label !== undefined && <span className="settings-file-label">{label}</span>}
    <code title={path}><bdi>{path}</bdi></code>
    <span className="settings-file-actions">
      {onOpen && <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />} disabled={disabled}
        aria-label={openLabel ?? `${open}: ${path}`} title={open} onClick={onOpen} />}
      <Button variant="minimal" size="small" icon={<AppIcon name="copy" size={15} />} aria-label={`${t("Copy path")}: ${path}`} title={t("Copy path")}
        onClick={() => { if (copy) copy(path); else void navigator.clipboard?.writeText(path).catch(() => { /* The path stays shown: it can be selected. */ }); }} />
      {onReveal && reveal && !missing && <Button variant="minimal" size="small" icon={<AppIcon name="openExternal" size={15} />} disabled={disabled}
        aria-label={`${t(reveal)}: ${path}`} title={t(reveal)} onClick={onReveal} />}
    </span>
  </span>;
}

const scopeLabel = (scope: string): MessageKey => scope === "Project" ? "Project" : scope === "BuiltIn" ? "Built-in" : "Global";
const targetOf = (location: HostLocation): SettingsFileTarget => ({ kind: location.kind, scope: location.scope, id: location.id });

/** The files of a page as the host lists them, and the two things a page does with one of them. */
export type SettingsFiles = Readonly<{
  locations: readonly HostLocation[]; platform: string | null;
  /** Opens a file or a folder in the code editor; undefined when this window opens none. */
  open?: (target: SettingsFileTarget) => void;
  reveal: (target: SettingsFileTarget) => void;
}>;

/** The files and the folders of a page, one row each, with the scope they belong to before the path. */
export function SettingsFileLocations({ files, disabled, copy }: { files: SettingsFiles; disabled?: boolean; copy?: (path: string) => void }) {
  const { t } = useShellLanguage();
  if (files.locations.length === 0) return null;
  return <div className="settings-file-locations" role="group" aria-label={t("Files")}>
    {files.locations.map(location => <SettingsFileLocation key={`${location.kind}:${location.scope}:${location.id ?? ""}`} path={location.path}
      label={<Tag minimal round>{t(scopeLabel(location.scope))}</Tag>} missing={!location.exists} readOnly={location.readOnly} platform={files.platform}
      disabled={disabled} copy={copy} onOpen={files.open && location.canOpen ? () => files.open!(targetOf(location)) : undefined}
      onReveal={() => files.reveal(targetOf(location))} />)}
  </div>;
}
