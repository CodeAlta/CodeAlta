import { useEffect, useState } from "react";
import { Button, InputGroup, Radio, RadioGroup } from "@blueprintjs/core";
import { worktrees } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { FolderPick } from "../folderPicker";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";

type Location = "global" | "project" | "custom";
const separator = (path: string) => path.includes("\\") ? "\\" : "/";

/**
 * Settings page for git worktrees: where the worktrees of sessions are created. A choice is saved at once, in
 * the configuration file of the user; the worktrees that exist stay where they are.
 */
export function WorktreeSettings({ epoch, pick, api = worktrees }: {
  epoch: string | null;
  /** Asks the operating system for a folder, starting from the one given. */
  pick?: (initialDirectory: string | null) => Promise<FolderPick>;
  api?: Pick<typeof worktrees, "settings" | "saveSettings">;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.settings({ expectedEpoch: epoch }, options), `${epoch}`);
  const [folder, setFolder] = useState("");
  useEffect(() => { setFolder(listing?.folder ?? ""); }, [listing?.folder]);
  const location: Location = listing?.location === "project" || listing?.location === "custom" ? listing.location : "global";
  const globalFolder = listing?.globalFolder ?? "";

  const save = (next: Location, path: string) => void mutate(async () => {
    const reply = await api.saveSettings({ expectedEpoch: epoch, location: next, folder: path.trim() || null }, { timeoutMilliseconds: 30_000 });
    // The codes of this service, in the words the settings pages have for them.
    return { status: reply.status === "invalid_request" ? "invalid" : reply.status === "save_failed" ? "write_failed" : reply.status };
  }, "Saved.");
  async function browse() {
    if (!pick) return;
    const picked = await pick(folder.trim() || null);
    if (picked.status !== "ok") return;
    setFolder(picked.path);
    save("custom", picked.path);
  }

  return <SettingsPage className="worktree-settings" label={t("Worktrees")} group="Agent & models" title="Worktrees"
    description="Where the git worktrees of sessions are created." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="worktree" title="Worktrees unavailable" /> : <>
      <RadioGroup className="worktree-settings-places" label={t("New worktrees go")} selectedValue={location} disabled={busy}
        onChange={event => {
          const next = event.currentTarget.value as Location;
          // A folder of the user's own is asked for before it can be the place.
          if (next === "custom" && !folder.trim()) void browse();
          else save(next, folder);
        }}>
        <Radio value="global">
          <span className="worktree-settings-place"><strong>{t("In your CodeAlta folder")}</strong>
            <code>{globalFolder}{separator(globalFolder)}{t("<project>")}{separator(globalFolder)}{t("<name>")}</code></span>
        </Radio>
        <Radio value="project">
          <span className="worktree-settings-place"><strong>{t("Inside each project")}</strong>
            <code>{t("<project>")}/.alta/worktrees/{t("<name>")}</code><small>{t("Git ignores this folder.")}</small></span>
        </Radio>
        <Radio value="custom">
          <span className="worktree-settings-place"><strong>{t("In a folder you choose")}</strong>
            {folder.trim() && <code>{folder.trim()}{separator(folder)}{t("<project>")}{separator(folder)}{t("<name>")}</code>}</span>
        </Radio>
      </RadioGroup>
      <div className="worktree-settings-folder">
        <InputGroup aria-label={t("Folder for worktrees")} placeholder={t("Folder for worktrees")} value={folder} disabled={busy} onValueChange={setFolder}
          onKeyDown={event => { if (event.key === "Enter" && folder.trim()) save(location === "custom" ? "custom" : location, folder); }}
          onBlur={() => { if (folder.trim() !== (listing.folder ?? "") && (folder.trim() || location !== "custom")) save(location, folder); }} />
        {pick && <Button icon={<AppIcon name="open" size={15} />} disabled={busy} onClick={() => void browse()}>{t("Browse…")}</Button>}
      </div>
    </>}
  </SettingsPage>;
}
