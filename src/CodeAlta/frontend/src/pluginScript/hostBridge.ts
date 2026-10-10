import { createContext } from "react";

/**
 * What only the shell of the window can do for the script of a plugin: open another canvas, show the changes of a project, and open the user guide.
 * The rest of `alta.host` is served by contexts the content already has (links, sessions, the commands of plugins, the toaster).
 * Null where the shell gives nothing (a fixture, a detached view): those requests do nothing.
 */
export type PluginHostBridge = Readonly<{
  openCanvas: (request: Readonly<{ pluginKey: string; canvasId: string; projectId: string | null; sessionId: string | null; key: string | null }>) => void;
  showChanges: (projectId: string | null) => void;
  /** Opens the Documentation tab: at a page of the guide and a heading of it, or where the reader was. */
  openDocumentation: (page: string | null, anchor: string | null) => void;
}>;

/** Given by the shell. */
export const PluginHostBridgeContext = createContext<PluginHostBridge | null>(null);
