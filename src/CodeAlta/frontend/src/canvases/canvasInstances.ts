// The instances that the canvas tabs of the window show, as the owner of the tabs keeps them: an instance closes with its tab.
import { fileTabKey, type FileTab } from "../fileTabs";
import type { CanvasHub } from "./canvasHub";
import { abandonedCanvas } from "./canvasPages";

export type CanvasInstances = ReturnType<typeof createCanvasInstances>;

/**
 * Keeps which instance each canvas tab shows, space by space, and says what becomes of an instance when its tab goes: a tab that is
 * closed closes its instance, and a tab that only leaves the page with its space keeps it, hidden.
 *
 * @param hub Closes and hides instances on the host.
 * @param openTabs The tabs a space has open now, whether the window shows the space or kept its tabs when it left it.
 */
export function createCanvasInstances(hub: Pick<CanvasHub, "close" | "setVisible">, openTabs: (space: string) => readonly FileTab[]) {
  const shown = new Map<string, string>();
  const keyOf = (tab: FileTab, space: string) => `${space}\n${fileTabKey(tab)}`;
  function close(tab: FileTab, space: string) {
    const key = keyOf(tab, space), instanceId = shown.get(key);
    if (!instanceId) return;
    shown.delete(key);
    void hub.close(instanceId);
  }

  return {
    /** The host opened the instance that a tab shows. */
    opened(tab: FileTab, space: string, instanceId: string) { shown.set(keyOf(tab, space), instanceId); },
    /** The tab is closed, not only taken out of the page: its instance closes, and the plugin lets go of what it held for it. */
    closed(tab: FileTab, space: string) { close(tab, space); },
    /**
     * The tab left the page with the instance it showed. A tab that is still one of its space (another space is shown, or the tab asks
     * again) keeps the instance. A tab that is no longer one was closed before the host had answered, so that closing it found no instance:
     * the instance it took meanwhile is closed now, since no tab shows it and nothing else would close it.
     */
    released(tab: FileTab, space: string) {
      if (abandonedCanvas(openTabs(space), tab) === "close") close(tab, space);
    },
    /**
     * The host opened an instance for a tab that went away meanwhile. A tab that was closed had no instance yet to close with it: this one
     * is closed. A tab that left the page with its space is still one of the tabs of that space: its instance is only hidden.
     */
    abandoned(tab: FileTab, space: string, instanceId: string) {
      if (abandonedCanvas(openTabs(space), tab) === "hide") void hub.setVisible(instanceId, false); else void hub.close(instanceId);
    },
  };
}
