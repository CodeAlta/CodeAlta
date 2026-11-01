import { useMemo, useState, type ReactNode } from "react";
import type { ScriptLoader } from "../pluginScript/scriptModule";
import { SettingsPage } from "../SettingsPage";
import { CanvasPanel } from "./CanvasPanel";
import type { CanvasHub } from "./canvasHub";
import { pluginSettingsTab, type PluginSettingsPage } from "./canvasPages";
import type { CanvasPluginControl } from "./canvasPlugin";

const nothing = () => { };

/**
 * The settings page of a plugin in Settings > Extensions: the canvas of placement Settings that the plugin declares, drawn as a tab draws
 * it (its fragment, its script, its calls), in the frame of a page of Settings with the title and the description of the canvas. Its one
 * instance opens when the page is shown; the owner closes it when the page is left, from what `onInstance` says. **Reload** asks the plugin
 * for the page again. While the plugin does not run the page says so, as a tab does.
 */
export function PluginCanvasSettingsPage({ page, hub, control, onInstance, onAbandoned, onLeave, onOpenSource, loadScript, settings }: {
  page: PluginSettingsPage;
  /** The settings the plugin declares, drawn above its canvas; absent when it declares none. */
  settings?: ReactNode;
  hub: CanvasHub;
  /** What can be done about a plugin that does not run; null when its folder is not known (a built-in plugin). */
  control: CanvasPluginControl | null;
  /** The instance the page shows, or null once it does not. */
  onInstance: (instanceId: string | null) => void;
  /** The host opened the instance for a page that was left meanwhile. */
  onAbandoned: (instanceId: string) => void;
  /** The page is left: its plugin closed it, or the person chose Close on the page of a plugin that does not run. */
  onLeave: () => void;
  /** Opens the code editor on the folder of the plugin. */
  onOpenSource: (folder: Readonly<{ id: string; path: string; name: string }>) => void;
  /** How the module of the page is loaded: the window imports it, a test gives its own. */
  loadScript?: ScriptLoader;
}) {
  const { pluginKey, canvasId, title, icon, plugin } = page;
  const tab = useMemo(() => pluginSettingsTab({ pluginKey, canvasId, title, icon, plugin, description: null }), [pluginKey, canvasId, title, icon, plugin]);
  // Reload draws the page again: the panel asks the host for the instance, which is the one that is open.
  const [generation, setGeneration] = useState(0);
  return <SettingsPage className="plugin-canvas-settings" label={title} group="Extensions" title={{ text: title }}
    description={page.description ? { text: page.description } : undefined} notice={null} loading={false} busy={false} onReload={() => setGeneration(value => value + 1)}>
    {settings}
    <CanvasPanel key={generation} tab={tab} spaceId={null} hub={hub} visible active onActivate={nothing} onLook={nothing}
      onInstance={onInstance} onAbandoned={instanceId => onAbandoned(instanceId)} onClose={onLeave} onOpenSource={onOpenSource} control={control} loadScript={loadScript} />
  </SettingsPage>;
}
