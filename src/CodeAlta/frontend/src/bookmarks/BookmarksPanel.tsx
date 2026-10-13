import { useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Button, InputGroup, NonIdealState, SegmentedControl, Tag } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { bookmarkMoment } from "./BookmarkRail";
import { bookmarkDetail, bookmarkFilters, bookmarkTitle, groupBookmarks, maximumBookmarkLabel, type Bookmark, type BookmarkFilter, type BookmarkProject, type BookmarkSessionGroup } from "./bookmarks";
import type { BookmarksHub } from "./bookmarksHub";

const filterLabels = { all: "All", message: "Messages", session: "Sessions" } as const;

/**
 * The bookmarks of every project: by project, then by session, with the messages of a session in the order of its
 * conversation. A bookmark opens its session at its message; it is named and removed here. A search and a filter narrow
 * the list. A session that no longer exists keeps its bookmarks until they are removed.
 */
export function BookmarksPanel({ hub, projects, onGo, onOpenSession, onActivate, onNotice }: {
  hub: BookmarksHub;
  /** The projects of the window, for their names. */
  projects: readonly BookmarkProject[];
  /** Goes to what a bookmark marks: its session, at its message for a message. */
  onGo: (bookmark: Bookmark) => void;
  onOpenSession: (sessionId: string) => void;
  onActivate?: () => void;
  /** Says that a change was refused. */
  onNotice?: (message: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [query, setQuery] = useState(""), [filter, setFilter] = useState<BookmarkFilter>("all");
  const [renaming, setRenaming] = useState<{ id: string; text: string } | null>(null);
  const saving = useRef(false);
  const groups = useMemo(() => groupBookmarks(state.bookmarks, projects, filter, query), [state.bookmarks, projects, filter, query]);
  const counts = useMemo(() => ({ all: state.bookmarks.length, message: state.bookmarks.filter(bookmark => bookmark.kind === "message").length,
    session: state.bookmarks.filter(bookmark => bookmark.kind === "session").length }), [state.bookmarks]);
  const refused = () => onNotice?.(t("The bookmark could not be changed."));
  async function remove(bookmark: Bookmark) { if (!(await hub.remove(bookmark.id)).ok) refused(); }
  async function rename() {
    if (!renaming || saving.current) return;
    const { id, text } = renaming;
    saving.current = true;
    try {
      const outcome = await hub.rename(id, text.trim() || null);
      if (!outcome.ok) refused();
    } finally { saving.current = false; setRenaming(current => current?.id === id ? null : current); }
  }
  const actions = (bookmark: Bookmark, title: string) => <span className="bookmark-actions">
    <Button variant="minimal" size="small" icon={<AppIcon name="edit" size={14} />} aria-label={t("Rename the bookmark: {title}", { title })} title={t("Rename")}
      onClick={() => setRenaming({ id: bookmark.id, text: bookmark.label ?? "" })} />
    <Button variant="minimal" size="small" icon={<AppIcon name="bookmarkRemove" size={14} />} aria-label={t("Remove the bookmark: {title}", { title })} title={t("Remove bookmark")}
      onClick={() => void remove(bookmark)} />
  </span>;
  const editor = (bookmark: Bookmark) => <form className="bookmark-rename" onSubmit={event => { event.preventDefault(); void rename(); }}>
    <InputGroup autoFocus size="small" maxLength={maximumBookmarkLabel} value={renaming!.text} aria-label={t("Name of the bookmark")} placeholder={bookmarkTitle({ ...bookmark, label: null })}
      onChange={event => setRenaming({ id: bookmark.id, text: event.target.value })}
      onKeyDown={event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); setRenaming(null); } }} />
    <Button type="submit" size="small" intent="primary" text={t("Save")} />
    <Button size="small" variant="minimal" text={t("Cancel")} onClick={() => setRenaming(null)} />
  </form>;
  const messageRow = (bookmark: Bookmark, missing: boolean) => {
    const title = bookmarkTitle(bookmark), detail = bookmarkDetail(bookmark);
    return <li key={bookmark.id} className="bookmark-row" data-bookmark-id={bookmark.id} data-role={bookmark.role ?? undefined}>
      {renaming?.id === bookmark.id ? editor(bookmark) : <>
        <button type="button" className="bookmark-open" disabled={missing} onClick={() => onGo(bookmark)}
          title={missing ? undefined : t("Go to this message")}>
          <span className="bookmark-role" aria-hidden="true"><AppIcon name={bookmark.role === "user" ? "user" : "assistant"} size={15} /></span>
          <span className="bookmark-text"><strong>{title}</strong>{detail && <span className="bookmark-detail">{detail}</span>}</span>
          <time dateTime={bookmark.messageAt ?? undefined} title={t("Bookmarked {date}", { date: bookmarkMoment(bookmark.createdAt, locale) })}>{bookmarkMoment(bookmark.messageAt, locale)}</time>
        </button>
        {actions(bookmark, title)}
      </>}
    </li>;
  };
  const sessionCard = (session: BookmarkSessionGroup) => {
    const whole = session.session;
    return <article key={session.sessionId} className="bookmarks-session" data-session-id={session.sessionId} data-missing={session.missing ? "true" : undefined}>
      <header className="bookmarks-session-heading">
        {whole && renaming?.id === whole.id ? editor(whole) : <>
          <button type="button" className="bookmarks-session-open" disabled={session.missing} onClick={() => whole ? onGo(whole) : onOpenSession(session.sessionId)}
            title={session.missing ? undefined : t("Open session")}>
            <AppIcon name={whole ? "bookmark" : "chat"} size={15} /><strong>{whole?.label?.trim() || session.title}</strong>
            {whole?.label?.trim() && <span className="bookmarks-session-title">{session.title}</span>}
          </button>
          {whole && <Tag minimal round className="bookmarks-session-tag">{t("Session bookmarked")}</Tag>}
          {session.missing && <Tag minimal round intent="warning">{t("Session deleted")}</Tag>}
          {whole && actions(whole, whole.label?.trim() || session.title)}
        </>}
      </header>
      {session.messages.length > 0 && <ul className="bookmarks-messages">{session.messages.map(bookmark => messageRow(bookmark, session.missing))}</ul>}
    </article>;
  };
  return <div className="bookmarks-panel" aria-label={t("Bookmarks")} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    <header className="bookmarks-header">
      <div className="bookmarks-heading"><AppIcon name="bookmark" size={18} /><h1>{t("Bookmarks")}</h1>{state.available && <span className="bookmarks-count">{counts.all}</span>}</div>
      {state.available && counts.all > 0 && <div className="bookmarks-tools">
        <InputGroup className="bookmarks-search" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query}
          aria-label={t("Search bookmarks")} placeholder={t("Search bookmarks")} onChange={event => setQuery(event.target.value)} />
        <SegmentedControl size="small" aria-label={t("Kind of bookmark")} value={filter} onValueChange={value => setFilter(value as BookmarkFilter)}
          options={bookmarkFilters.map(value => ({ value, label: `${t(filterLabels[value])} ${counts[value]}` }))} />
      </div>}
    </header>
    <div className="bookmarks-list">
      {!state.loaded ? <p role="status" className="bookmarks-status">{t("Loading…")}</p>
        : !state.available ? <NonIdealState icon={<AppIcon name="bookmark" size={40} />} title={t("Bookmarks are not available here.")} />
        : counts.all === 0 ? <NonIdealState icon={<AppIcon name="bookmark" size={40} />} title={t("No bookmarks yet")}
          description={t("Bookmark a message with the bookmark button of its row, or a session from its menu.")} />
        : groups.length === 0 ? <NonIdealState icon={<AppIcon name="search" size={40} />} title={t("No bookmark matches.")} />
        : groups.map(project => <section key={project.projectId ?? ""} className="bookmarks-project" data-project-id={project.projectId ?? ""}>
          <h2><AppIcon name={project.projectId ? "folder" : "chat"} size={15} /><span>{project.projectId ? project.name : t("Chats")}</span><span className="bookmarks-count">{project.count}</span></h2>
          {project.sessions.map(sessionCard)}
        </section>)}
      {state.truncated && <p role="status" className="bookmarks-status">{t("There are more bookmarks than this list shows.")}</p>}
    </div>
  </div>;
}
