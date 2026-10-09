import { HTMLSelect } from "@blueprintjs/core";
import type { SessionPermissionModeChoice } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { permissionModeDescription, permissionModeName } from "./permissionModes";
import { useShellLanguage } from "./shellLanguage";

/** The permission mode on the chip of the session configuration, in the warning color when Claude Code skips the review. */
export function PermissionChipPart({ id, skipsReview }: { id: string; skipsReview: boolean }) {
  const { t } = useShellLanguage();
  const name = permissionModeName(id);
  return <span className="composer-selection-part composer-permission-part" data-skips-review={skipsReview || undefined}
    title={skipsReview ? t("Claude Code runs requests without CodeAlta's review in this mode.") : undefined}>
    <AppIcon name={skipsReview ? "warning" : "shield"} size={14} /><span>{name ? t(name) : id}</span></span>;
}

/**
 * The permission mode of a session: first the mode of its provider (no mode of its own), then the modes the provider
 * offers. A mode in which Claude Code runs requests without CodeAlta's review is marked, and described under the list.
 */
export function PermissionModeSelect({ id, value, modes, providerMode, disabled = false, onChange }: {
  id: string; value: string | null; modes: readonly SessionPermissionModeChoice[]; providerMode: string | null; disabled?: boolean;
  onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  const label = (mode: string) => { const name = permissionModeName(mode); return name ? t(name) : mode; };
  const describe = (mode: string | null) => { const description = mode && permissionModeDescription(mode); return description ? t(description) : null; };
  const shown = value ?? providerMode;
  const skipsReview = shown !== null && modes.some(mode => mode.id === shown && mode.skipsReview);
  const description = shown === null ? t("The mode the settings of Claude Code choose.") : describe(shown);
  return <div className="composer-permission">
    <HTMLSelect fill id={id} aria-label={t("Permissions")} aria-describedby={`${id}-description`} value={value ?? ""} disabled={disabled}
      onChange={event => onChange(event.target.value)}>
      <option value="" title={providerMode ?? undefined}>{providerMode ? t("Provider setting ({mode})", { mode: label(providerMode) }) : t("The setting of the CLI")}</option>
      {/* A saved mode the provider no longer lists stays visible, as a saved model does. */}
      {value !== null && !modes.some(mode => mode.id === value) && <option value={value}>{`${label(value)} · ${t("Unverified")}`}</option>}
      {modes.map(mode => <option key={mode.id} value={mode.id} title={`${mode.id} · ${describe(mode.id) ?? ""}`}>
        {mode.skipsReview ? `⚠ ${label(mode.id)}` : label(mode.id)}</option>)}
    </HTMLSelect>
    <p id={`${id}-description`} className="composer-permission-description" data-skips-review={skipsReview || undefined}>
      {skipsReview && <AppIcon name="warning" size={14} />}
      <span>{description}{shown !== null && <> <code>{shown}</code></>}</span>
    </p>
  </div>;
}
