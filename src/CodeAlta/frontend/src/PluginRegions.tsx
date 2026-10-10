import { useEffect, useState } from "react";
import type { PluginUiScopeRequest } from "#neoastra";
import { MarkdownContent } from "./MarkdownContent";
import { PluginHtml } from "./PluginHtml";
import { pluginRegions, pluginsChangedEvent, samePluginRegions, type PluginRegionView } from "./pluginUi";

const refreshMilliseconds = 10_000;
type Read = (request: PluginUiScopeRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;

/**
 * What plugins show around the prompt of a pane, read like the status items: when the composer appears,
 * every ten seconds while it stays, when the window gets the focus back and when the plugins change.
 */
export function usePluginRegions(epoch: string | null, projectId: string | null, sessionId: string | null, read: Read): readonly PluginRegionView[] {
  const [shown, setShown] = useState<{ projectId: string | null; sessionId: string | null; items: readonly PluginRegionView[] }>();
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    let reading = false;
    const refresh = () => {
      if (reading || document.visibilityState === "hidden") return;
      reading = true;
      void read({ expectedEpoch: epoch, projectId, sessionId }, { signal: abort.signal, timeoutMilliseconds: 8000 })
        .then(reply => {
          const items = abort.signal.aborted ? null : pluginRegions(reply, projectId, sessionId);
          if (items) setShown(previous => previous && previous.projectId === projectId && previous.sessionId === sessionId
            && samePluginRegions(previous.items, items) ? previous : { projectId, sessionId, items });
        }, () => { /* The content simply stays as last read. */ })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    window.addEventListener("focus", refresh);
    window.addEventListener(pluginsChangedEvent, refresh);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener("focus", refresh); window.removeEventListener(pluginsChangedEvent, refresh); };
  }, [epoch, projectId, sessionId, read]);
  return epoch && shown?.projectId === projectId && shown.sessionId === sessionId ? shown.items : [];
}

/** The plugin content of one place of a composer: `footer` above the status line, `inline` in it. */
export function PluginRegionSlot({ epoch, projectId, sessionId, region, read }: {
  epoch: string | null; projectId: string | null; sessionId: string | null; region: "footer" | "inline"; read: Read;
}) {
  const items = usePluginRegions(epoch, projectId, sessionId, read);
  const shown = items.some(item => region === "footer" ? item.region === "footer" : item.region !== "footer");
  if (!shown) return null;
  const content = <PluginRegionContent items={items} region={region} projectId={projectId} sessionId={sessionId} />;
  return region === "footer" ? <div className="composer-plugin-footer">{content}</div> : content;
}

/** The contents of one place: `footer` above the prompt, `bar` and `status` in the status line. */
export function PluginRegionContent({ items, region, projectId, sessionId }: {
  items: readonly PluginRegionView[]; region: "footer" | "inline"; projectId: string | null; sessionId: string | null;
}) {
  const shown = items.filter(item => region === "footer" ? item.region === "footer" : item.region !== "footer");
  if (shown.length === 0) return null;
  const pane = { projectId, sessionId };
  return <>{shown.map(item => <span key={item.id} className={`plugin-region plugin-region-${item.region}`}>
    {item.html ? <PluginHtml html={item.html} pluginKey={item.pluginKey} pane={pane} script={{ path: item.script, problem: item.scriptProblem }} />
      : item.markdown ? <MarkdownContent source={item.markdown} /> : item.text}
  </span>)}</>;
}
