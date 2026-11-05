import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { worktrees as worktreesApi } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { worktreesReply, type Worktree } from "../worktrees/worktrees";

export type EditorWorktreesApi = Pick<typeof worktreesApi, "list">;

// What the host answers when there is no repository to list: what was known is no longer so. Any other failure is a
// moment of trouble, which keeps the list.
const lasting = new Set(["not_repository", "unknown_project", "project_unavailable", "unavailable", "invalid"]);

/**
 * The worktrees of a project's repository as a button in the header of its files, which opens the code editor of the
 * one that is chosen: in a tab of its own (or the tab it already has), never in this one, so that the files open here
 * and the ones that are not saved always come from the checkout the tab is named for. Nothing is shown for a repository
 * with a single checkout, or while the host cannot say.
 */
export function EditorWorktrees({ epoch, projectId, projectName, visible, onOpen, api = worktreesApi }: {
  epoch: string; projectId: string; projectName: string;
  /** The tab is shown: the worktrees are read then, and again when the list is opened. */
  visible: boolean;
  /** The code editor of this worktree is wanted. */
  onOpen: (worktree: Worktree) => void;
  api?: EditorWorktreesApi;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [worktrees, setWorktrees] = useState<readonly Worktree[] | null>(null);
  const reading = useRef<AbortController | null>(null);

  // The last read is the one that counts: an older answer never replaces it.
  const read = useCallback(() => {
    reading.current?.abort();
    const abort = reading.current = new AbortController();
    void api.list({ expectedEpoch: epoch, projectId }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => worktreesReply(reply, projectId), () => "read_failed")
      .then(value => {
        if (abort.signal.aborted) return;
        // A failure of the moment leaves what was known; one that says there is nothing to list takes the button away.
        if (typeof value === "string") { if (lasting.has(value)) { setWorktrees(null); setOpen(false); } return; }
        setWorktrees(value.worktrees);
        // The button goes with the last other checkout: its list must not stay open for the next time it comes back.
        if (value.worktrees.length < 2) setOpen(false);
      });
  }, [api, epoch, projectId]);
  useEffect(() => {
    if (!visible) return;
    read();
    return () => reading.current?.abort();
  }, [visible, read]);

  if (!worktrees || worktrees.length < 2) return null;
  const content = <div className="branch-switcher" role="group" aria-label={t("Worktrees")}>
    <Menu className="branch-switcher-list" role="listbox" aria-label={t("Worktrees")}>
      {worktrees.map(worktree => {
        const name = worktree.project ? projectName : worktree.name;
        return <MenuItem key={worktree.path} roleStructure="listoption" selected={worktree.project} disabled={worktree.missing}
          icon={<AppIcon name={worktree.project ? "folder" : "worktree"} size={14} />} text={name}
          label={worktree.missing ? t("Folder gone") : worktree.branch ?? worktree.head ?? undefined}
          title={worktree.missing ? t("The folder of this worktree is gone.") : worktree.project ? t("This editor shows the folder of the project.") : worktree.folder}
          onClick={() => { if (!worktree.project) onOpen(worktree); }} />;
      })}
    </Menu>
  </div>;

  return <PopoverNext content={content} placement="bottom-end" popoverClassName="branch-switcher-popover" isOpen={open}
    // The list is read again each time it opens.
    onInteraction={next => { setOpen(next); if (next && !open) read(); }}>
    <Button variant="minimal" size="small" icon={<AppIcon name="worktree" size={15} />} aria-label={t("Open a worktree in the editor")}
      title={t("Open a worktree in the editor")} aria-haspopup="listbox" aria-expanded={open} />
  </PopoverNext>;
}
