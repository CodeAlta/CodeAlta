import { useEffect, useState } from "react";
import { Button, Card, HTMLSelect, InputGroup, Switch, Tag } from "@blueprintjs/core";
import type { WindowsDisplay, WindowsWindow } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ProviderIconPicker } from "../ProviderIconFields";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { spaceBrand, spaceColors } from "../spaces/spaces";
import { WindowIcon, windowName } from "./WindowBadge";
import type { WindowsHub, WindowsState } from "./windowsHub";

/**
 * Settings > Windows: whether the windows come back at a start; the main window and the windows that are open or kept,
 * with their name, icon, color and the monitor each goes to; and the monitors that are connected.
 */
export function WindowSettings({ hub, state }: { hub: WindowsHub; state: WindowsState }) {
  const { t } = useShellLanguage();
  const [displays, setDisplays] = useState<readonly WindowsDisplay[] | null>(null);
  // The monitors are read again when the windows change: a monitor that is connected moves the windows pinned to it.
  useEffect(() => {
    let alive = true;
    void hub.displays().then(value => { if (alive) setDisplays(value); });
    return () => { alive = false; };
  }, [hub, state.windows]);
  useEffect(() => { void hub.refresh(); }, [hub]);
  const windows = state.windows.filter(window => window.main || window.open || window.saved);
  return <div className="configuration-page settings-card-page" aria-label={t("Windows")}>
    <header className="page-heading"><span className="eyebrow">{t("Personalization")}</span><h1>{t("Windows")}</h1></header>
    <div className="settings-grid">
      <Card className="appearance-settings window-settings" aria-labelledby="settings-windows-title">
        <h2 id="settings-windows-title">{t("Windows")}</h2>
        <p className="settings-field-help">{t("Tabs moved out of the main window open in windows of their own. Name a window to keep it: it opens again where it was.")}</p>
        <Field label={t("Restore windows at startup")} htmlFor="settings-windows-restore"
          notice={<p className="settings-field-help">{t("The windows that were open when CodeAlta ended come back where they were, with their tabs.")}</p>}>
          <Switch id="settings-windows-restore" className="settings-checkbox" checked={state.restoreAtStartup}
            onChange={event => void hub.setRestoreAtStartup(event.currentTarget.checked)} />
        </Field>
        <div className="window-settings-list" role="list">
          {windows.map(window => <WindowRow key={window.id} hub={hub} window={window} displays={displays} current={state.current?.id === window.id} />)}
        </div>
        {!windows.some(window => !window.main && window.saved) && <p className="settings-field-help">{t("No window is kept yet: name a window to keep it.")}</p>}
      </Card>
      <Card className="appearance-settings window-settings" aria-labelledby="settings-monitors-title">
        <h2 id="settings-monitors-title">{t("Monitors")}</h2>
        {displays && !displays.length && <p className="settings-field-help">{t("The monitors are not known on this system.")}</p>}
        <ul className="window-monitors">
          {(displays ?? []).map(display => <li key={display.id}>
            <AppIcon name="window" size={15} />
            <span className="window-monitor-name">{display.name || display.id}</span>
            <span className="muted-text">{t("{width} × {height}, {scale} %", { width: display.width, height: display.height, scale: display.scale })}</span>
            {display.primary && <Tag minimal round>{t("Primary")}</Tag>}
          </li>)}
        </ul>
      </Card>
    </div>
  </div>;
}

/** One window: its name, icon and color, the monitor it goes to, and what opens, shows or forgets it. */
function WindowRow({ hub, window, displays, current }: { hub: WindowsHub; window: WindowsWindow; displays: readonly WindowsDisplay[] | null; current: boolean }) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(window.name);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  useEffect(() => setName(window.name), [window.name]);
  const id = `settings-window-${window.id}`;
  const shown = windowName(t, window);
  async function save(change: Parameters<WindowsHub["setIdentity"]>[1]) {
    setBusy(true);
    const status = await hub.setIdentity(window.id, change);
    setBusy(false);
    setFailed(status !== "ok");
  }
  const commitName = () => {
    const value = name.trim();
    if (value === window.name || value.length > 64 || (window.main && !value)) { setName(window.name); return; }
    void save({ name: value });
  };
  // A pinned monitor that is not connected stays listed, said as such: the window opens on the primary one meanwhile.
  const missing = window.pinnedDisplayId && !(displays ?? []).some(display => display.id === window.pinnedDisplayId);
  return <div className="window-settings-row" role="listitem" aria-labelledby={`${id}-title`}>
    <div className="window-settings-head">
      <WindowIcon window={window} size={18} />
      <strong id={`${id}-title`}>{shown}</strong>
      <Tag minimal round intent={window.open ? "success" : undefined}>{t(window.open ? "Open now" : "Closed now")}</Tag>
      <span className="window-settings-actions">
        {!window.main && !window.open && <Button size="small" icon={<AppIcon name="open" size={14} />} disabled={busy}
          onClick={() => void hub.open({ savedId: window.id })}>{t("Open")}</Button>}
        {window.open && !current && <Button size="small" icon={<AppIcon name="eye" size={14} />} onClick={() => hub.focus(window.id)}>{t("Focus")}</Button>}
        {!window.main && !window.open && <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={busy}
          onClick={() => void hub.forget(window.id)}>{t("Forget")}</Button>}
      </span>
    </div>
    <Field label={t("Name")} htmlFor={`${id}-name`}>
      <InputGroup id={`${id}-name`} value={name} maxLength={64} spellCheck={false} autoComplete="off" disabled={busy}
        onChange={event => setName(event.target.value)} onBlur={commitName}
        onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); else if (event.key === "Escape") { event.stopPropagation(); setName(window.name); } }} />
    </Field>
    <Field label={t("Icon")} htmlFor={`${id}-icon`}>
      <ProviderIconPicker id={`${id}-icon`} value={window.icon ?? ""} automatic={{ icon: null }} shown={spaceBrand(window)} disabled={busy}
        fallback="window" symbolsFirst onChange={icon => void save({ icon })} />
    </Field>
    <Field label={t("Color")}>
      <span className="space-color-swatches" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={busy}
          aria-pressed={window.color?.toLowerCase() === color} aria-label={color} title={color}
          onClick={() => void save({ color: window.color?.toLowerCase() === color ? "" : color })} />)}
      </span>
    </Field>
    <Field label={t("Monitor")} htmlFor={`${id}-monitor`}
      notice={missing && <p className="settings-field-help warning-text">{t("Not connected: opens on the primary monitor")}</p>}>
      <HTMLSelect id={`${id}-monitor`} value={window.pinnedDisplayId ?? ""} disabled={busy || displays === null}
        onChange={event => void save({ pinnedDisplayId: event.target.value })}>
        <option value="">{t("Where it was last")}</option>
        {(displays ?? []).map(display => <option key={display.id} value={display.id}>{display.name || display.id}</option>)}
        {missing && <option value={window.pinnedDisplayId!}>{t("{name} (not connected)", { name: window.pinnedDisplayName || window.pinnedDisplayId! })}</option>}
      </HTMLSelect>
    </Field>
    {!window.main && <Field label={t("Keep this window")} htmlFor={`${id}-saved`}
      notice={<p className="settings-field-help">{t("A window that is kept opens again where it was, from Move to window, after it is closed.")}</p>}>
      <Switch id={`${id}-saved`} className="settings-checkbox" checked={window.saved} disabled={busy} onChange={event => void save({ saved: event.currentTarget.checked })} />
    </Field>}
    {failed && <p className="settings-field-help error-text" role="alert">{t("The window could not be changed.")}</p>}
  </div>;
}
