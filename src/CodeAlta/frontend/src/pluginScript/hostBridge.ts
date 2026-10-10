import { createContext } from "react";

/**
 * What only the shell of the window can do for the script of a plugin: open another canvas, and show the changes of a project.
 * The rest of `alta.host` is served by contexts the content already has (links, sessions, the commands of plugins, the toaster).
 * Null where the shell gives nothing (a fixture, a detached view): those requests do nothing.
 */
export type PluginHostBridge = Readonly<{
  openCanvas: (request: Readonly<{ pluginKey: string; canvasId: string; projectId: string | null; sessionId: string | null; key: string | null }>) => void;
  showChanges: (projectId: string | null) => void;
}>;

/** Given by the shell. */
export const PluginHostBridgeContext = createContext<PluginHostBridge | null>(null);
