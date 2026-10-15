import { HTMLSelect } from "@blueprintjs/core";
import type { SessionPermissionModeChoice } from "#neoastra";
import { permissionModeDescription, permissionModeName } from "./permissionModes";
import { useShellLanguage } from "./shellLanguage";

/**
 * The permission mode of a session, a row of the form of the Agent chip: a select like the other rows, which names the
 * mode the session runs in and lists the modes it can be given. The mode a session without one runs in (the one of its
 * provider, else the one of the application) is marked **Default**; choosing it gives the session no mode of its own.
 * The description of the mode shown is the tooltip of the select.
 */
export function PermissionModeSelect({ id, value, modes, defaultMode, disabled = false, onChange }: {
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
  const marked = (text: string) => `${text} · ${t("Default")}`;
  // The default is one of the modes of the list, where it is marked, or an entry of its own: a mode a session
  // cannot be given (the plan mode of a provider), or the setting of the CLI.
  const listed = defaultMode !== null && modes.some(mode => mode.id === defaultMode);
  // The default mode is the entry "": choosing it gives the session no mode of its own.
  const selected = value === null || value === defaultMode ? "" : value;
  const shown = value ?? defaultMode;
  const description = shown === null ? undefined : permissionModeDescription(shown);
  const name = shown === null ? t("The setting of the CLI") : label(shown);
  return <HTMLSelect fill id={id} className="composer-permission" disabled={disabled} value={selected} data-own={value !== null || undefined}
    aria-label={`${t("Permissions")}: ${name}`} title={description ? t(description) : name}
    onChange={event => { if (event.target.value !== selected) onChange(event.target.value); }}>
    {!listed && <option value="">{marked(defaultMode === null ? t("The setting of the CLI") : label(defaultMode))}</option>}
    {/* A saved mode that is no longer offered stays visible, as a saved model does. */}
    {value !== null && value !== defaultMode && !modes.some(mode => mode.id === value) && <option value={value}>{`${label(value)} · ${t("Unverified")}`}</option>}
    {modes.map(mode => <option key={mode.id} value={mode.id === defaultMode ? "" : mode.id}>{mode.id === defaultMode ? marked(label(mode.id)) : label(mode.id)}</option>)}
  </HTMLSelect>;
}
