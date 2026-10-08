import { Button, PopoverNext } from "@blueprintjs/core";
import type { DesktopShellPreferences, desktopShell } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { commandDefinitions, type CommandId } from "./commandRegistry";
import type { MessageKey } from "./localization";
import { useShellLanguage } from "./shellLanguage";

/** The commands that zoom the window. */
export type WindowZoomCommand = Extract<CommandId, "zoomIn" | "zoomOut" | "resetZoom">;

/** The smallest and the largest zoom of the window, in percent: the ends of the host's steps (`DesktopPreferences.ZoomSteps`). */
export const minimumWindowZoom = 25, maximumWindowZoom = 500;

/**
 * Asks the host for a zoom command, and gives `apply` the preferences it answers with: they hold the zoom the
 * window now has, whoever asked (a key, a typed command or the control of the title bar).
 */
export async function zoomWindow(command: WindowZoomCommand, zoom: typeof desktopShell.zoom, apply: (preferences: DesktopShellPreferences) => void): Promise<void> {
  const value = await zoom({ direction: command === "zoomIn" ? 1 : command === "zoomOut" ? -1 : 0 }, { timeoutMilliseconds: 8_000 });
  if (value.status === "ok") apply(value);
}

/**
 * The zoom of the window in the title bar, before the theme switch: its percentage, which opens one step out,
 * back to 100% and one step in. The buttons run the commands the keyboard runs.
 */
export function WindowZoom({ zoom, run }: { zoom: number; run: (command: WindowZoomCommand) => void }) {
  const { t } = useShellLanguage();
  // What a button does and the key that does the same.
  const title = (label: MessageKey, command: WindowZoomCommand) => {
    const key = commandDefinitions.find(definition => definition.id === command)?.keys?.[0];
    return key ? `${t(label)} (${key})` : t(label);
  };
  return <PopoverNext placement="bottom-end" content={<div className="window-zoom-steps" role="toolbar" aria-label={t("Zoom")}>
    <Button variant="minimal" size="small" icon={<AppIcon name="zoomOut" size={14} />} aria-label={t("Zoom out")} title={title("Zoom out", "zoomOut")}
      disabled={zoom <= minimumWindowZoom} onClick={() => run("zoomOut")} />
    <Button variant="minimal" size="small" className="window-zoom-value" aria-label={t("Reset Zoom")} title={title("Reset Zoom", "resetZoom")} onClick={() => run("resetZoom")}>{zoom}%</Button>
    <Button variant="minimal" size="small" icon={<AppIcon name="zoomIn" size={14} />} aria-label={t("Zoom in")} title={title("Zoom in", "zoomIn")}
      disabled={zoom >= maximumWindowZoom} onClick={() => run("zoomIn")} />
  </div>}>
    <Button variant="minimal" size="small" className="window-zoom" aria-label={`${t("Zoom")}: ${zoom}%`} title={t("Zoom")}>{zoom}%</Button>
  </PopoverNext>;
}
