import { useSyncExternalStore } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { BookmarksHub } from "./bookmarksHub";

/** The mark of a bookmarked session on its row. The row follows this one fact: a bookmark elsewhere renders nothing here. */
export function SessionBookmarkMark({ hub, sessionId }: { hub: BookmarksHub; sessionId: string }) {
  const { t } = useShellLanguage();
  const read = () => hub.getSnapshot().bookmarks.some(bookmark => bookmark.kind === "session" && bookmark.sessionId === sessionId);
  const bookmarked = useSyncExternalStore(hub.subscribe, read, read);
  return bookmarked ? <span className="session-bookmark-mark" role="img" aria-label={t("This session is bookmarked")} title={t("This session is bookmarked")}><AppIcon name="bookmark" size={12} /></span> : null;
}
