import { useEffect, useState } from "react";
import { Button, InputGroup, PopoverNext, Switch } from "@blueprintjs/core";
import type { WindowsWindow } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { Logo } from "../ProviderIcon";
import { ProviderIconPicker } from "../ProviderIconFields";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { spaceBrand, spaceColors } from "../spaces/spaces";
import type { WindowsHub } from "./windowsHub";

type Translate = ReturnType<typeof useShellLanguage>["t"];

/** The name a window is shown with: the one the user gave, else Main window or Window 2, 3… */
export function windowName(t: Translate, window: Readonly<{ main: boolean; name: string }>): string {
  if (window.main && (window.name === "Main" || !window.name)) return t("Main window");
  const numbered = /^Window (\d+)$/.exec(window.name);
  return numbered ? t("Window {number}", { number: Number(numbered[1]) }) : window.name;
}

/** The icon of a window, in its color. */
export function WindowIcon({ window, size = 16 }: { window: Readonly<{ icon: string | null; color: string | null }>; size?: number }) {
  return <span className="window-icon"><Logo brand={spaceBrand(window)} size={size} fallback="window" /></span>;
}

/**
 * The badge of a secondary window, at the left of its tab strip: its icon, its name and its space, and what changes
 * them (name, icon, color, whether it is kept when it is closed) and closes it.
 */
export function WindowBadge({ hub, window, spaceName, onClose }: {
  hub: WindowsHub; window: WindowsWindow; spaceName: string | null;
  /** Closes the window: its tabs close, as each would, and then the window. */
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(window.name);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  useEffect(() => setName(window.name), [window.name]);
  const shown = windowName(t, window);
  async function save(change: Parameters<WindowsHub["setIdentity"]>[1]) {
    setBusy(true);
    const status = await hub.setIdentity(window.id, change);
    setBusy(false);
    setFailed(status !== "ok");
  }
  const commitName = () => {
    const value = name.trim();
    if (value === window.name || value.length > 64) { setName(window.name); return; }
    void save({ name: value });
  };
  return <PopoverNext isOpen={open} onInteraction={setOpen} placement="bottom-start" content={<div className="window-badge-popover" data-neoastra-no-drag>
    <Field label={t("Name of the window")} htmlFor="window-badge-name">
      <InputGroup id="window-badge-name" value={name} maxLength={64} spellCheck={false} autoComplete="off" disabled={busy} autoFocus
        onChange={event => setName(event.target.value)} onBlur={commitName}
        onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); else if (event.key === "Escape") { event.stopPropagation(); setName(window.name); } }} />
    </Field>
    <Field label={t("Icon")} htmlFor="window-badge-icon">
      <ProviderIconPicker id="window-badge-icon" value={window.icon ?? ""} automatic={{ icon: null }} shown={spaceBrand(window)} disabled={busy}
        fallback="window" symbolsFirst onChange={icon => void save({ icon })} />
    </Field>
    <Field label={t("Color")}>
      <span className="space-color-swatches" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={busy}
          aria-pressed={window.color?.toLowerCase() === color} aria-label={color} title={color}
          onClick={() => void save({ color: window.color?.toLowerCase() === color ? "" : color })} />)}
      </span>
    </Field>
    <Field label={t("Keep this window")} htmlFor="window-badge-saved"
      notice={<p className="settings-field-help">{t("A window that is kept opens again where it was, from Move to window, after it is closed.")}</p>}>
      <Switch id="window-badge-saved" className="settings-checkbox" checked={window.saved} disabled={busy} onChange={event => void save({ saved: event.currentTarget.checked })} />
    </Field>
    {failed && <p className="settings-field-help error-text" role="alert">{t("The window could not be changed.")}</p>}
    <div className="window-badge-actions">
      <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="close" size={14} />} onClick={() => { setOpen(false); onClose(); }}>{t("Close window")}</Button>
    </div>
  </div>}>
    <Button variant="minimal" size="small" className="window-badge" data-neoastra-no-drag
      aria-label={t("Window: {name}", { name: shown })} title={[t("Window: {name}", { name: shown }), spaceName ? t("Space: {space}", { space: spaceName }) : null].filter(Boolean).join("\n")}
      icon={<WindowIcon window={window} size={15} />}>
      <span className="window-badge-name">{shown}</span>
      {spaceName && <span className="window-badge-space">{spaceName}</span>}
    </Button>
  </PopoverNext>;
}
