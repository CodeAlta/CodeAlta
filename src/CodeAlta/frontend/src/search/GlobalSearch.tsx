import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { SessionReferenceSearchResponse, WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { commandKeys, rankCommands, type CommandId } from "../commandRegistry";
import { fileAppearance, splitProjectPath } from "../fileAppearance";
import { KeyGesture } from "../KeyGesture";
import type { MessageKey } from "../localization";
import { searchPluginCommands, type PluginCommandView } from "../pluginUi";
import { sessionTime } from "../sessionTime";
import { plainTitle } from "../sessionTitle";
import { useShellLanguage } from "../shellLanguage";
import { flatResults, matchRanges, nextCategory, readQuery, searchCategories, searchProjects, searchSessions, shownGroups, suggestedCommands,
  type CommandResult, type FileResult, type SearchCategory, type SearchGroup, type SearchResult } from "./searchResults";

const pageStep = 8;
const categoryLabels: Readonly<Record<SearchCategory, MessageKey>> = { all: "All", sessions: "Sessions", projects: "Projects", files: "Files", commands: "Commands" };
type FileProject = Pick<WorkspaceProject, "id" | "name" | "path">;
type FoundFiles = Readonly<{ query: string; project: string; items: readonly FileResult[]; state: "idle" | "loading" | "indexing" | "ready" | "failed" }>;
const noFiles: FoundFiles = { query: "", project: "", items: [], state: "idle" };

/** Where the search starts: its category, what is typed in it, and the project whose sessions it keeps to (null for the chats). */
export type SearchStart = Readonly<{ category?: SearchCategory; text?: string; projectId?: string | null }>;

/** What was chosen in the search, for the window to open once the search has closed. */
export type SearchChoice = Readonly<{ kind: "command"; id: CommandId } | { kind: "plugin"; id: string } | { kind: "project"; id: string }
  | { kind: "session"; projectId: string | null; id: string } | { kind: "file"; project: Pick<WorkspaceProject, "id" | "path">; path: string }>;

/** A text with the parts that the query found marked. */
function Marked({ text, words }: { text: string; words: readonly string[] }) {
  const ranges = matchRanges(text, words);
  if (!ranges.length) return <>{text}</>;
  const parts: ReactNode[] = [];
  let at = 0;
  for (const [start, end] of ranges) {
    if (start > at) parts.push(text.slice(at, start));
    parts.push(<mark key={start}>{text.slice(start, end)}</mark>);
    at = end;
  }
  if (at < text.length) parts.push(text.slice(at));
  return <>{parts}</>;
}

/**
 * The search of the window (Ctrl+P, the search button of the title bar, or "/" in an empty prompt): one field
 * that looks through the sessions of every project and the chats, the projects, the files of the project in front
 * and the commands. Enter opens what is selected. Tab changes the category; a text that starts with "/" looks for a
 * command. It is a movable, resizable window like the others; its place and size are remembered.
 */
export function GlobalSearch({ snapshot, favorites, start, files, note, available, onCommand, pluginCommands = [], onPluginCommand, onProject, onSession, onFile, onClose }: {
  snapshot: WorkspaceSnapshot | null;
  /** What the list of the projects and sessions leaves out, when the host shortened it. */
  note?: string | null;
  /** The favorite projects, which come first among projects that match as well. */
  favorites: readonly string[];
  start: SearchStart;
  /** The project whose files are searched, and the search itself; null where no file can be searched. */
  files: Readonly<{ project: FileProject; search: (query: string, signal: AbortSignal) => Promise<SessionReferenceSearchResponse> }> | null;
  available: (id: CommandId) => boolean;
  onCommand: (id: CommandId) => void;
  /** The commands of plugins, listed after the application's own. */
  pluginCommands?: readonly PluginCommandView[];
  onPluginCommand?: (id: string) => void;
  onProject: (project: WorkspaceProject) => void;
  onSession: (session: WorkspaceSession) => void;
  onFile: (project: FileProject, path: string) => void;
  onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const [text, setText] = useState(start.text ?? "");
  const [category, setCategory] = useState<SearchCategory>(start.category ?? "all");
  // The project whose sessions the search keeps to: undefined for every project and the chats.
  const [scope, setScope] = useState<string | null | undefined>(start.projectId);
  const [active, setActive] = useState(0);
  const [foundFiles, setFoundFiles] = useState<FoundFiles>(noFiles);
  const [attempt, setAttempt] = useState(0);
  const [now] = useState(() => Date.now());
  const query = useMemo(() => readQuery(text), [text]);
  const words = query.words;
  const scoped = scope !== undefined;
  const shownCategory: SearchCategory = scoped ? "sessions" : query.commands ? "commands" : category;
  const latest = useRef({ files, available, onCommand, onPluginCommand }); latest.current = { files, available, onCommand, onPluginCommand };

  // The files of the project in front are searched by the host, a moment after the last key.
  const fileQuery = words.join(" ");
  const fileProject = files?.project.id ?? "";
  const wantFiles = !!files && !scoped && !query.commands && fileQuery !== "" && (category === "all" || category === "files");
  useEffect(() => {
    const current = latest.current.files;
    if (!wantFiles || !current) { setFoundFiles(noFiles); return; }
    const controller = new AbortController();
    setFoundFiles(value => value.query === fileQuery && value.project === fileProject ? value : { ...value, state: "loading" });
    const timer = setTimeout(() => {
      void current.search(fileQuery, controller.signal).then(page => {
        if (controller.signal.aborted) return;
        if (!["ok", "indexing", "incomplete"].includes(page.status)) { setFoundFiles({ query: fileQuery, project: fileProject, items: [], state: "failed" }); return; }
        // A file is what the code editor opens: the folders the host also finds are left out. The host ranks them.
        const items = page.items.filter(item => !item.directory).map((item, index): FileResult => ({ kind: "file", key: `file:${fileProject}:${item.path}`,
          score: 2000 + index, path: item.path, directory: false, project: current.project }));
        setFoundFiles({ query: fileQuery, project: fileProject, items, state: page.status === "indexing" ? "indexing" : "ready" });
      }, () => { if (!controller.signal.aborted) setFoundFiles({ query: fileQuery, project: fileProject, items: [], state: "failed" }); });
    }, attempt === 0 ? 90 : 400);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [wantFiles, fileQuery, fileProject, attempt]);
  // A project that is still being indexed is asked again, for a while.
  useEffect(() => { if (foundFiles.state === "indexing" && attempt < 40) setAttempt(value => value + 1); }, [foundFiles]);
  useEffect(() => { setAttempt(0); }, [fileQuery, fileProject]);

  const found = useMemo(() => {
    const none: SearchResult[] = [];
    const own = rankCommands(text, command => t(command.label), command => t(command.description))
      .filter(item => item.command.id !== "palette")
      .map(({ command, score }): CommandResult => ({ kind: "command", key: `command:${command.id}`, score, name: command.name, label: t(command.label),
        description: t(command.description), group: t(command.category), keys: commandKeys(command), enabled: true, run: () => latest.current.onCommand(command.id) }));
    // Nothing typed: the commands that start something come first.
    if (!words.length) own.sort((left, right) => rank(left.name) - rank(right.name));
    const plugins = searchPluginCommands(text, pluginCommands).map((command, index): CommandResult => ({ kind: "command", key: `plugin:${command.id}`, score: 3000 + index,
      name: command.name, label: command.label, description: command.description, group: command.group ?? command.plugin, keys: command.keys ? [command.keys] : [],
      enabled: true, run: () => latest.current.onPluginCommand?.(command.id) }));
    return {
      sessions: query.commands || !snapshot ? none : searchSessions(snapshot, words, scope),
      projects: query.commands || scoped || !snapshot ? none : searchProjects(snapshot, words, favorites),
      files: query.commands || scoped ? none : foundFiles.items as readonly SearchResult[],
      commands: scoped ? none : [...own, ...plugins] as readonly SearchResult[],
    };
  }, [snapshot, text, scope, favorites, pluginCommands, foundFiles, locale]);

  const groups = useMemo(() => shownGroups(shownCategory, found), [shownCategory, found]);
  const rows = useMemo(() => flatResults(groups), [groups]);
  const index = Math.max(0, Math.min(active, rows.length - 1));
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [index, groups]);

  const enabled = (result: SearchResult) => result.kind !== "command" || !result.key.startsWith("command:") || latest.current.available(result.key.slice("command:".length) as CommandId);
  function open(result: SearchResult | undefined) {
    if (!result || !enabled(result)) return;
    if (result.kind === "project") onProject(result.project);
    else if (result.kind === "session") onSession(result.session);
    else if (result.kind === "file") onFile(result.project, result.path);
    else result.run();
  }
  const move = (delta: number) => { if (rows.length) setActive((index + delta + rows.length * pageStep) % rows.length); };
  const choose = (value: SearchCategory) => { setCategory(value); setActive(0); input.current?.focus(); };
  const counts: Readonly<Record<SearchGroup, number>> = { sessions: found.sessions.length, projects: found.projects.length, files: found.files.length, commands: found.commands.length };
  const scopeName = !scoped ? null : scope === null ? t("Chats") : snapshot?.projects.find(project => project.id === scope)?.name ?? t("Unavailable project");
  const groupLabel = (group: SearchGroup) => group === "sessions" && !words.length && shownCategory === "all" ? t("Recent sessions")
    : group === "files" && files ? t("Files of {name}", { name: files.project.name }) : t(categoryLabels[group]);
  const searching = wantFiles && (foundFiles.state === "loading" || foundFiles.state === "indexing");
  const empty: MessageKey = shownCategory === "files" && !files ? "Open a project to search its files."
    : shownCategory === "files" && !words.length ? "Type the name of a file."
    : shownCategory === "files" && foundFiles.state === "failed" ? "Project files could not be read."
    : searching ? "Searching…" : "Nothing matches.";

  let option = -1;
  return <AppWindow storageKey="codealta.desktop.window.search.v1" className="global-search" titleId="search-title"
    title={<><AppIcon name="search" size={14} /> {t("Search")}</>}
    preferredSize={viewport => ({ width: Math.min(880, viewport.width - 32), height: Math.min(620, viewport.height - 64) })}
    minimumSize={{ width: 420, height: 260 }} onClose={onClose} closeLabel={t("Close")} onOpened={() => input.current?.focus()}
    onClick={event => { if (event.target === event.currentTarget) onClose(); }}
    onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      const plain = !event.ctrlKey && !event.altKey && !event.metaKey;
      const handled = event.key === "Escape" ? (onClose(), true)
        : event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (setActive(Math.min(rows.length - 1, index + pageStep)), true)
        : event.key === "PageUp" ? (setActive(Math.max(0, index - pageStep)), true)
        : event.key === "Enter" ? (open(rows[index]), true)
        // Tab goes through the categories; the buttons of the window are reached with the pointer.
        : event.key === "Tab" && plain && !scoped && !query.commands ? (choose(nextCategory(category, event.shiftKey ? -1 : 1)), true)
        : event.key === "Tab" && plain ? true
        // Backspace in an empty field lets go of the project the search keeps to.
        : event.key === "Backspace" && scoped && text === "" ? (setScope(undefined), setCategory("sessions"), setActive(0), true) : false;
      if (handled) event.preventDefault();
    }}>
      <div className="global-search-box"><AppIcon name="search" size={17} />
        {scoped && <span className="global-search-scope"><AppIcon name={scope === null ? "chat" : "folder"} size={13} /> {scopeName}
          <button type="button" aria-label={t("Search every project")} title={t("Search every project")}
            onClick={() => { setScope(undefined); setCategory("sessions"); setActive(0); input.current?.focus(); }}><AppIcon name="close" size={12} /></button></span>}
        <input ref={input} id="search-input" type="text" role="combobox" aria-autocomplete="list" aria-expanded="true" spellCheck={false} autoComplete="off"
          aria-controls="search-results" aria-activedescendant={rows[index] ? `search-option-${index}` : undefined}
          placeholder={t(scoped ? "Search sessions" : "Search sessions, projects, files and commands…")} aria-label={t("Search")} value={text}
          onChange={event => { setText(event.target.value); setActive(0); }} />
        {searching && <ActivitySpinner size={13} />}
      </div>
      {!scoped && <div className="global-search-categories" role="tablist" aria-label={t("Search in")}>
        {searchCategories.map(value => <button key={value} type="button" role="tab" tabIndex={-1} aria-controls="search-results" aria-selected={value === shownCategory}
          data-empty={value !== "all" && words.length > 0 && counts[value] === 0 || undefined}
          disabled={query.commands && value !== "commands"} onClick={() => choose(value)}>
          {t(categoryLabels[value])}{value !== "all" && words.length > 0 && <span className="global-search-count">{counts[value]}</span>}</button>)}
      </div>}
      <div ref={list} id="search-results" className="global-search-results" role="listbox" aria-label={t("Results")}>
        {groups.map(group => <div key={group.group} role="group" aria-label={groupLabel(group.group)}>
          <div className="global-search-group" role="presentation"><span>{groupLabel(group.group)}</span>
            {shownCategory === "all" && group.total > group.results.length && <button type="button" tabIndex={-1} onClick={() => choose(group.group)}>
              {t("Show all {count}", { count: group.total })}<AppIcon name="chevronRight" size={12} /></button>}</div>
          {group.results.map(result => { option++; const at = option;
            return <SearchRow key={result.key} result={result} words={words} id={`search-option-${at}`} selected={at === index} enabled={enabled(result)} locale={locale} now={now}
              onHover={() => { if (at !== index) setActive(at); }} onOpen={() => open(result)} />; })}
        </div>)}
        {!rows.length && <p className="global-search-empty" role="status">{t(empty)}</p>}
        {note && !query.commands && <p className="global-search-note" role="note">{note}</p>}
      </div>
      <div className="global-search-hints">
        <span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("open")}</span>
        {!scoped && <span><kbd>Tab</kbd> {t("category")}</span>}{!scoped && <span><kbd>/</kbd> {t("commands")}</span>}
        <span className="global-search-hint-end"><kbd>Esc</kbd> {t("close")}</span>
      </div>
  </AppWindow>;
}

// The commands that start something, in the order they are offered when nothing is typed; the others follow.
const rank = (name: string) => { const at = suggestedCommands.indexOf(name); return at < 0 ? suggestedCommands.length : at; };

/** One result: its icon, its name with what the query found marked, what tells it from the others, and what is known of it. */
function SearchRow({ result, words, id, selected, enabled, locale, now, onHover, onOpen }: {
  result: SearchResult; words: readonly string[]; id: string; selected: boolean; enabled: boolean; locale: Parameters<typeof sessionTime>[1]; now: number;
  onHover: () => void; onOpen: () => void;
}) {
  const { t } = useShellLanguage();
  let icon: IconName, tone: string, title: ReactNode, detail: ReactNode = null, meta: ReactNode = null, hint: string;
  if (result.kind === "project") {
    const { project } = result;
    icon = project.archived ? "archive" : "folder"; tone = project.archived ? "muted" : "gold"; hint = `${project.name}\n${project.path}`;
    title = <Marked text={project.name} words={words} />;
    detail = <Marked text={project.path} words={words} />;
    meta = <>{result.favorite && <span className="global-search-star" role="img" aria-label={t("Favorite")} title={t("Favorite")}><AppIcon name="star" size={12} /></span>}
      {project.archived ? <span className="global-search-tag">{t("Archived")}</span>
        : result.sessions > 0 && <span>{t(result.sessions === 1 ? "1 session" : "{count} sessions", { count: result.sessions })}</span>}</>;
  } else if (result.kind === "session") {
    const { session, project } = result;
    const automated = !!session.automationId;
    icon = result.child ? "childSession" : automated ? "automation" : session.projectId === null ? "chat" : "assistant";
    tone = result.child ? "teal" : automated ? "gold" : "purple";
    const name = plainTitle(session.title);
    hint = `${name}\n${project?.name ?? t("Chat")}`;
    title = <Marked text={name} words={words} />;
    detail = session.projectId === null ? t("Chat") : <Marked text={project?.name ?? t("Unavailable project")} words={words} />;
    const time = sessionTime(session.updatedAt, locale, Math.max(now, Date.parse(session.updatedAt) || 0));
    meta = <time dateTime={time.dateTime} title={time.title}>{time.label}</time>;
  } else if (result.kind === "file") {
    const look = fileAppearance(result.path, false), { name, parent } = splitProjectPath(result.path);
    icon = look.icon; tone = look.tone; hint = result.path;
    title = <Marked text={name} words={words} />;
    detail = parent;
  } else {
    icon = "chevronRight"; tone = "azure"; hint = `/${result.name}\n${result.description}`;
    title = <>{result.label}<code className="global-search-slash">/{result.name}</code></>;
    detail = result.description;
    meta = <span className="global-search-keys">{result.keys.slice(0, 2).map(gesture => <KeyGesture key={gesture} gesture={gesture} />)}</span>;
  }
  return <div role="option" id={id} aria-selected={selected} aria-disabled={!enabled} className="global-search-row" data-kind={result.kind} title={hint}
    onMouseMove={onHover} onClick={onOpen}>
    <span className="global-search-icon" data-file-tone={tone}><AppIcon name={icon} size={15} /></span>
    <span className="global-search-title">{title}</span>
    <span className="global-search-detail">{detail}</span>
    <span className="global-search-meta">{meta}</span>
  </div>;
}
