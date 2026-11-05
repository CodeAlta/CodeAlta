import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Button } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { isMacPlatform } from "../commandRegistry";
import type { LoadAllRequest, OlderHistoryState } from "../HistoryPanel";
import { useShellLanguage } from "../shellLanguage";
import type { useTimelinePosition } from "../timelineScroll";
import { isFindKey } from "./findInPage";
import { useFindInPage } from "./useFindInPage";

/**
 * The search in the timeline of a session, the box of a rendered page over the rows that are loaded: the text the
 * rows show, with the folded work that holds it opened while it is looked for. The turns not read yet are not searched:
 * the count says so, and the box reads them on request. What it puts in view is where the reader then is, not the end.
 */
export function useTimelineFind(timeline: Pick<ReturnType<typeof useTimelinePosition>, "elementRef" | "moved">, available: boolean, onMoved?: () => void) {
  const { t } = useShellLanguage();
  const [older, setOlder] = useState<OlderHistoryState>("none");
  const loadAll = useRef<LoadAllRequest | null>(null);
  const loading = t("Loading previous messages…"), missing = t("Only the loaded messages are searched. Load all messages to search them too.");
  const finder = useFindInPage({
    page: () => timeline.elementRef.current?.querySelector<HTMLElement>(".messages") ?? null,
    available, label: "Find in this session",
    // A timeline is read from its end, and it grows while it is searched.
    endFirst: true, refreshDelay: 200, partial: older !== "none",
    extra: older === "loading" ? <span className="session-find-loading" role="status" title={loading}><ActivitySpinner size={13} label={loading} /></span>
      : older === "more" ? <Button variant="minimal" size="small" icon={<AppIcon name="history" size={15} />} aria-label={t("Load all messages")} title={missing} onClick={() => loadAll.current?.()} />
      : null,
    onMoved: () => { onMoved?.(); timeline.moved(); },
  });
  const history = useMemo(() => ({ find: finder.pattern, onOlderState: setOlder, loadAllRequest: loadAll }), [finder.pattern]);
  // What the element around the session takes its keys with. Ctrl+F before what has the keyboard: the text editor of the
  // prompt has a search of its own. Escape after it, which closes what is open there first.
  const keys = {
    "data-find-keys": finder.available || undefined,
    onKeyDownCapture: (event: KeyboardEvent) => { if (isFindKey(event, isMacPlatform()) && finder.keyDown(event)) event.stopPropagation(); },
    onKeyDown: (event: KeyboardEvent) => { if (finder.keyDown(event)) event.stopPropagation(); },
  };
  return { finder, history, keys };
}
