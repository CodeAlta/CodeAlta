import { useContext, useEffect, useState } from "react";
import { Button } from "@blueprintjs/core";
import type { ComposerStatusRequest } from "#neoastra";
import { AppIcon, type IconName } from "./AppIcon";
import { composerStatusItems, sameComposerStatus, type ComposerStatusView } from "./pluginStatus";
import { PluginUiContext, pluginsChangedEvent } from "./pluginUi";
import { shortStatusText } from "./promptSurface/promptSurface";
import { useSurfaceDensity } from "./promptSurface/PromptSurfaceParts";
import { settingsNavigation } from "./settingsNavigation";
import { useShellLanguage } from "./shellLanguage";

const refreshMilliseconds = 10_000;
type Read = (request: ComposerStatusRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;
const icons: Readonly<Record<string, IconName>> = { mcp: "server" };

/**
 * The plugin status items at the end of the composer's status line (the MCP servers and the tools the
 * session activated, for example). They are read when the composer appears, every ten seconds while it
 * stays, when the window gets the focus back and when the plugins change, so a change made in Settings or by
 * a run shows up without a reload. An item that names a Settings page opens it; one that names a plugin
 * command runs it.
 */
export function ComposerStatus({ epoch, projectId, sessionId, read }: { epoch: string | null; projectId: string | null; sessionId: string | null; read: Read }) {
  const { t } = useShellLanguage();
  const plugins = useContext(PluginUiContext);
  const [shown, setShown] = useState<{ projectId: string | null; sessionId: string | null; items: readonly ComposerStatusView[] }>();
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    let reading = false;
    const refresh = () => {
      if (reading || document.visibilityState === "hidden") return;
      reading = true;
      void read({ expectedEpoch: epoch, projectId, sessionId }, { signal: abort.signal, timeoutMilliseconds: 8000 })
        .then(reply => {
          const items = abort.signal.aborted ? null : composerStatusItems(reply, projectId);
          if (items) setShown(previous => previous && previous.projectId === projectId && previous.sessionId === sessionId
            && sameComposerStatus(previous.items, items) ? previous : { projectId, sessionId, items });
        }, () => { /* The items simply stay as last read. */ })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    window.addEventListener("focus", refresh);
    window.addEventListener(pluginsChangedEvent, refresh);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener("focus", refresh); window.removeEventListener(pluginsChangedEvent, refresh); };
  }, [epoch, projectId, sessionId, read]);
  const density = useSurfaceDensity();
  const items = epoch && shown?.projectId === projectId && shown.sessionId === sessionId ? shown.items : [];
  if (items.length === 0) return null;
  return <span className="composer-plugin-status">{items.map(item => {
    // The whole text where there is room; its first part ("1/1") otherwise, the whole in the tooltip.
    const text = item.text && density !== "wide" ? shortStatusText(item.text) : item.text;
    const about = [item.label, item.text].filter(Boolean).join(" ");
    const content = <>{icons[item.pluginId] && <AppIcon name={icons[item.pluginId]} size={14} />}
      {item.label && <strong>{item.label}</strong>}{text && <span className="composer-plugin-status-text">{text}</span>}
      <span className="composer-plugin-status-dot" aria-hidden="true" /></>;
    return item.settingsPage
      ? <Button key={item.key} variant="minimal" size="small" className="composer-plugin-status-item" data-tone={item.tone} title={`${about}\n${t("Open settings")}`}
        onClick={() => settingsNavigation.open(item.settingsPage!)}>{content}</Button>
      : item.commandId
      ? <Button key={item.key} variant="minimal" size="small" className="composer-plugin-status-item" data-tone={item.tone}
        title={[about, plugins.contributions.commands.find(command => command.id === item.commandId)?.description].filter(Boolean).join("\n")}
        onClick={() => plugins.run(item.commandId!, { projectId, sessionId })}>{content}</Button>
      : <span key={item.key} className="composer-plugin-status-item" data-tone={item.tone} title={about}>{content}</span>;
  })}</span>;
}
