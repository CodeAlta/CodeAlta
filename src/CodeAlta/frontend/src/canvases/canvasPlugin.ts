// What a tab of a canvas can do about the plugin that is not running: say why, build it again, open its source.
import type { plugins as pluginsService } from "#neoastra";

export type CanvasPluginApi = Pick<typeof pluginsService, "list" | "reload">;

/** What the host says of the package of a plugin whose canvas does not show. */
export type CanvasPluginProbe = Readonly<{
  /** `running`, `stopped`, `failed` or `disabled`; `unknown` when the package is not among those the host lists. */
  state: "running" | "stopped" | "failed" | "disabled" | "unknown";
  /** The first reason the plugin did not start, or null. */
  message: string | null;
  /** The id of the folder of the package and its path, for the code editor; null when the package is not listed. */
  folder: Readonly<{ id: string; path: string; name: string }> | null;
}>;

export type CanvasPluginControl = Readonly<{
  /** Reads what the host says of the package now. */
  probe: () => Promise<CanvasPluginProbe>;
  /** Builds the package again and loads it; `ok` when the new version runs. */
  rebuild: () => Promise<Readonly<{ ok: boolean; message: string | null }>>;
}>;

/**
 * Reads the id of a plugin folder the host gave (`plugin:global:<package>`, `plugin:project:<project>:<package>`):
 * where the plugin is, as the plugins page of the host names it. Null for anything else.
 */
export function readPluginFolder(id: string | null | undefined): Readonly<{ id: string; scope: "Global" | "Project"; projectId: string | null; packageId: string }> | null {
  if (!id || id.length > 512) return null;
  const global = /^plugin:global:([A-Za-z0-9][A-Za-z0-9._-]{0,127})$/u.exec(id);
  if (global) return { id, scope: "Global", projectId: null, packageId: global[1] };
  const project = /^plugin:project:([^:\s]{1,256}):([A-Za-z0-9][A-Za-z0-9._-]{0,127})$/u.exec(id);
  return project ? { id, scope: "Project", projectId: project[1], packageId: project[2] } : null;
}

/** The control of the plugin a tab names, or null when the tab does not say where the plugin is (a built-in plugin). */
export function createCanvasPluginControl(api: CanvasPluginApi, epoch: string | null, folderId: string | null | undefined): CanvasPluginControl | null {
  const folder = readPluginFolder(folderId);
  if (!folder || !epoch) return null;
  return {
    async probe() {
      try {
        const reply = await api.list({ expectedEpoch: epoch, projectId: folder.projectId }, { timeoutMilliseconds: 30_000 });
        const entry = reply.status === "ok" ? reply.plugins.find(candidate => candidate.folder === folder.id) : undefined;
        if (!entry) return { state: "unknown", message: null, folder: null };
        const state = entry.runtime === "running" || entry.runtime === "failed" || entry.runtime === "stopped" ? entry.runtime
          : !entry.enabled || entry.runtime === "disabled" ? "disabled" : "stopped";
        return { state, message: entry.errors?.[0] ?? entry.runtimeMessage ?? null, folder: entry.path ? { id: folder.id, path: entry.path, name: entry.name } : null };
      } catch { return { state: "unknown", message: null, folder: null }; }
    },
    async rebuild() {
      try {
        const reply = await api.reload({ expectedEpoch: epoch, projectId: folder.projectId, scope: folder.scope, id: folder.packageId }, { timeoutMilliseconds: 120_000 });
        return { ok: reply.status === "ok", message: reply.message };
      } catch { return { ok: false, message: null }; }
    },
  };
}
