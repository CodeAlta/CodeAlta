import { useCallback, useEffect, useState } from "react";
import { settingsFiles, type SettingsFileLocation as HostLocation } from "#neoastra";
import type { SettingsFiles, SettingsFileTarget } from "./SettingsFileLocation";
import type { SettingsNotice } from "./settingsEditing";

/** The part of the host that says where the files of a page are, opens one and shows one; a test gives its own. */
export type SettingsFilesApi = Pick<typeof settingsFiles, "list" | "open" | "reveal">;

/**
 * The files and the folders a page of Settings reads, as the host lists them, with the action that opens one in the
 * code editor (the window then leaves Settings) and the one that shows it in the file manager. `revision` changes
 * when the page read its own list again: a file that was created since is then there.
 */
export function useSettingsFiles({ page, epoch, projectId, revision, onOpened, setNotice, api = settingsFiles }: {
  page: string; epoch: string | null | undefined; projectId: string | null; revision?: unknown;
  /** Called once the code editor was asked to show the file; without it nothing is opened from the page. */
  onOpened?: () => void;
  setNotice: (notice: SettingsNotice | null) => void;
  api?: SettingsFilesApi;
}): SettingsFiles {
  const [listing, setListing] = useState<{ locations: readonly HostLocation[]; platform: string | null }>({ locations: [], platform: null });
  useEffect(() => {
    if (!epoch) { setListing({ locations: [], platform: null }); return; }
    const controller = new AbortController();
    void api.list({ expectedEpoch: epoch, projectId, page }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (!controller.signal.aborted) setListing(value.status === "ok" ? { locations: value.locations, platform: value.platform } : { locations: [], platform: null });
    }, () => { if (!controller.signal.aborted) setListing({ locations: [], platform: null }); });
    return () => controller.abort();
  }, [api, page, epoch, projectId, revision]);
  const request = useCallback((target: SettingsFileTarget) => ({ expectedEpoch: epoch ?? null, projectId, kind: target.kind, scope: target.scope, id: target.id ?? null, part: target.part ?? null }),
    [epoch, projectId]);
  const open = useCallback((target: SettingsFileTarget) => {
    void api.open(request(target), { timeoutMilliseconds: 15000 }).then(result => {
      if (result.status === "ok") onOpened?.();
      else setNotice({ key: result.status === "not_found" ? "The file could not be found." : "The file could not be opened.", intent: "danger" });
    }, () => setNotice({ key: "The file could not be opened.", intent: "danger" }));
  }, [api, request, onOpened, setNotice]);
  const reveal = useCallback((target: SettingsFileTarget) => {
    void api.reveal(request(target), { timeoutMilliseconds: 15000 }).then(result => {
      if (result.status !== "ok") setNotice({ key: "The file manager could not be opened.", intent: "warning" });
    }, () => setNotice({ key: "The file manager could not be opened.", intent: "warning" }));
  }, [api, request, setNotice]);
  return { locations: listing.locations, platform: listing.platform, open: epoch && onOpened ? open : undefined, reveal };
}
