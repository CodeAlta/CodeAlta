import { Button, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { SessionPermissionModeChoice } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { permissionModeDescription, permissionModeName } from "./permissionModes";
import { useShellLanguage } from "./shellLanguage";

/**
 * The permission mode of a session, on the line of its composer: a small button that names the mode the session runs
 * in and opens the modes it can be given. The mode a session without one runs in (the one of its provider, else the
 * one of the application) is marked in the list; choosing it gives the session no mode of its own.
 */
export function PermissionModeMenu({ id, value, modes, defaultMode, disabled = false, onChange }: {
  id: string;
  /** The mode chosen for the session, or null when it runs in the default one. */
  value: string | null;
  modes: readonly SessionPermissionModeChoice[];
  /** The mode a session without one runs in; null where the host leaves it to the CLI of the provider. */
  defaultMode: string | null; disabled?: boolean;
  /** Called with a mode, or with "" for no mode of its own. */
  onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  const label = (mode: string) => { const name = permissionModeName(mode); return name ? t(name) : mode; };
  const describe = (mode: string) => { const description = permissionModeDescription(mode); return description ? t(description) : undefined; };
  const shown = value ?? defaultMode;
  const name = shown === null ? t("The setting of the CLI") : label(shown);
  // The default is one of the modes of the list, where it is marked, or an entry of its own: a mode a session
  // cannot be given (the plan mode of a provider), or the setting of the CLI.
  const listed = defaultMode !== null && modes.some(mode => mode.id === defaultMode);
  const item = (mode: string, text: string, selected: boolean, chosen: string, description?: string, isDefault = false) =>
    <MenuItem key={mode || "default"} roleStructure="listoption" selected={selected} multiline className="permission-mode-item"
      text={<><span className="permission-mode-name">{text}</span>{description && <span className="permission-mode-description">{description}</span>}</>}
      label={isDefault ? t("Default") : undefined} onClick={() => { if (!selected) onChange(chosen); }} />;
  const menu = <Menu aria-label={t("Permissions")} className="permission-mode-menu">
    <MenuDivider title={t("Permissions")} />
    {!listed && item("", defaultMode === null ? t("The setting of the CLI") : label(defaultMode), value === null, "",
      defaultMode === null ? undefined : describe(defaultMode), true)}
    {/* A saved mode that is no longer offered stays visible, as a saved model does. */}
    {value !== null && !modes.some(mode => mode.id === value) && item(value, `${label(value)} · ${t("Unverified")}`, true, value)}
    {modes.map(mode => item(mode.id, label(mode.id), shown === mode.id, mode.id === defaultMode ? "" : mode.id, describe(mode.id), mode.id === defaultMode))}
  </Menu>;
  return <PopoverNext content={menu} placement="top-start" disabled={disabled}>
    <Button id={id} variant="minimal" size="small" className="composer-permission" disabled={disabled} data-own={value !== null || undefined}
      aria-label={`${t("Permissions")}: ${name}`} title={`${t("Permissions")}: ${name}`} icon={<AppIcon name="shield" size={14} />}>
      <span className="composer-permission-name">{name}</span>
    </Button>
  </PopoverNext>;
}
