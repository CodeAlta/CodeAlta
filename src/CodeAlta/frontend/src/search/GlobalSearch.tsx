import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { CanvasItem, LocalSearchHit, LocalSearchQueryResponse, SessionReferenceSearchResponse, WorkspaceProject, WorkspaceSession, WorkspaceSnapshot, WorkspaceClaudeCodeConversation } from "#neoastra";
import type { ClaudeCodeConversations } from "../claudeCodeImport/claudeCodeSessions";
import { canvasCommands, matchesCanvasCommand } from "../canvases/canvasPages";
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
import { contentIcon, contentKinds, contentKindsFor, contentResults, markedParts, titleWords, type ContentGroup, type ContentResult } from "./contentResults";
import { categoryLimit, flatResults, matchRanges, nextCategory, readQuery, searchArchivedSessions, searchCategories, searchProjects, searchSessions, shownGroups, suggestedCommands, searchClaudeCodeConversations,
  type CommandResult, type FileResult, type SearchCategory, type SearchGroup, type SearchResult } from "./searchResults";
import { searchSkills, type SkillChoice } from "./skillResults";
import { skillSourceLabels } from "../skillSources";

const pageStep = 8;
const categoryLabels: Readonly<Record<SearchCategory, MessageKey>> = { all: "All", sessions: "Sessions", conversations: "Conversations", projects: "Projects", files: "Files",
  commands: "Commands", skills: "Skills", docs: "Docs", tools: "Tools", archived: "Archived" };
const contentCategories: readonly SearchCategory[] = ["conversations", "docs", "tools"];
type FileProject = Pick<WorkspaceProject, "id" | "name" | "path">;
type FoundFiles = Readonly<{ query: string; project: string; items: readonly FileResult[]; state: "idle" | "loading" | "indexing" | "ready" | "failed" }>;
const noFiles: FoundFiles = { query: "", project: "", items: [], state: "idle" };
type FoundContent = Readonly<{ key: string; items: Readonly<Record<ContentGroup, readonly ContentResult[]>>; state: "idle" | "loading" | "ready" | "failed" }>;
const noContent: FoundContent = { key: "", items: { conversations: [], docs: [], tools: [] }, state: "idle" };
/** The index of the host, as the search window asks it. */
export type ContentSearch = Readonly<{ search: (query: string, kinds: readonly string[], projectId: string | null, signal: AbortSignal) => Promise<LocalSearchQueryResponse> }>;
const noArchived: readonly WorkspaceSession[] = [];
const noSkills: readonly SkillChoice[] = [];
// What a text that starts with "/" can show: commands and skills together, or one of them.
const slashCategories: readonly SearchCategory[] = ["all", "commands", "skills"];

/** Where the search starts: its category, what is typed in it, and the project whose sessions it keeps to (null for the chats). */
export type SearchStart = Readonly<{ category?: SearchCategory; text?: string; projectId?: string | null }>;

/** What was chosen in the search, for the window to open once the search has closed. */
export type SearchChoice = Readonly<{ kind: "command"; id: CommandId } | { kind: "plugin"; id: string } | { kind: "project"; id: string }
  | { kind: "canvas"; pluginKey: string; id: string }
  /** A skill the session in front is asked to use, with what was typed after its name. */
  | { kind: "skill"; name: string; args: string }
  /** What the index of the host found: a message, a section of the guide, a tool. */
  | { kind: "content"; hit: LocalSearchHit }
  | { kind: "session"; projectId: string | null; id: string } | { kind: "file"; project: Pick<WorkspaceProject, "id" | "path">; path: string }
  | { kind: "claude-code"; conversation: WorkspaceClaudeCodeConversation; providerId: string | null }>;

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
 * the commands and the skills of the project in front. Enter opens what is selected, and asks the session in front to
 * use a skill. Tab changes the category; a text that starts with "/" looks for a command or a skill, and what follows
 * the name of a skill goes with it. It is a movable, resizable window like the others; its place and size are remembered.
 */
export function GlobalSearch({ snapshot, favorites, start, files, note, available, onCommand, pluginCommands = [], onPluginCommand, canvases = [], canvasAvailable, onCanvas, onProject, onSession, onFile, onClose,
  archived = noArchived, archivedProjects, onRestore, skills = noSkills, onSkill, content = null, onContent, onOfferMeaning, claudeCode = null, onClaudeCode }: {
  snapshot: WorkspaceSnapshot | null;
  /** The archived sessions of the shown space: a query finds them in a group of their own, and choosing one restores it. */
  archived?: readonly WorkspaceSession[];
  /** The projects of the catalog, which name the project of an archived session that the shown space does not list. */
  archivedProjects?: readonly WorkspaceProject[];
  onRestore?: (session: WorkspaceSession) => void;
  /** The Claude Code sessions started elsewhere, not in CodeAlta yet: found among the sessions; null while there are none to search. */
  claudeCode?: ClaudeCodeConversations | null;
  /** A Claude Code session started elsewhere was chosen: its preview opens. */
  onClaudeCode?: (conversation: WorkspaceClaudeCodeConversation, providerId: string | null) => void;
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
  /** The canvases that plugins declare: each is a command that opens it, listed with the plugins' commands. */
  canvases?: readonly CanvasItem[];
  /** Whether a canvas can be opened for what the window has selected: a canvas about a project needs one. */
  canvasAvailable?: (item: CanvasItem) => boolean;
  onCanvas?: (item: CanvasItem) => void;
  onProject: (project: WorkspaceProject) => void;
  onSession: (session: WorkspaceSession) => void;
  onFile: (project: FileProject, path: string) => void;
  /** The skills of the project in front, and what asks the session in front to use one; none where no session can be asked. */
  skills?: readonly SkillChoice[];
  onSkill?: (name: string, args: string) => void;
  /** The index of the host: the conversations, the guide and the tools; none where the host has no index or search is off. */
  content?: ContentSearch | null;
  onContent?: (hit: LocalSearchHit) => void;
  /** Opens the dialog of search by meaning; given while it is offered. */
  onOfferMeaning?: () => void;
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
  const offersSkills = skills.length > 0 && !!onSkill;
  // A text that starts with "/" shows the commands, or the commands and the skills, or one of the two.
  const shownCategory: SearchCategory = scoped ? "sessions" : !query.commands ? category : !offersSkills ? "commands"
    : slashCategories.includes(category) ? category : "all";
  const latest = useRef({ files, content, available, onCommand, onPluginCommand, canvasAvailable, onCanvas }); latest.current = { files, content, available, onCommand, onPluginCommand, canvasAvailable, onCanvas };
  const skillSearch = useMemo(() => offersSkills ? searchSkills(skills, text) : [], [skills, text, offersSkills]);

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

  // The conversations, the guide and the tools are found by the index of the host, a moment after the last key. A
  // search kept to a project finds the conversations of that project.
  const [foundContent, setFoundContent] = useState<FoundContent>(noContent);
  const contentQuery = query.commands ? "" : text.trim();
  const askedKinds = scoped ? scope === null ? [] : contentKinds.conversations : contentKindsFor(category);
  const contentKey = `${contentQuery}\u0000${askedKinds.join(",")}\u0000${scoped ? scope ?? "" : ""}`;
  const wantContent = !!content && contentQuery !== "" && askedKinds.length > 0;
  useEffect(() => {
    const current = latest.current.content;
    if (!wantContent || !current) { setFoundContent(noContent); return; }
    const controller = new AbortController();
    setFoundContent(value => value.key === contentKey ? value : { ...value, state: "loading" });
    const timer = setTimeout(() => {
      void current.search(contentQuery, askedKinds, scoped ? scope ?? null : null, controller.signal).then(reply => {
        if (controller.signal.aborted) return;
        setFoundContent(reply.status === "ok" ? { key: contentKey, items: contentResults(reply.hits, readQuery(contentQuery).words), state: "ready" } : { ...noContent, key: contentKey, state: "failed" });
      }, () => { if (!controller.signal.aborted) setFoundContent({ ...noContent, key: contentKey, state: "failed" }); });
    }, 120);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [wantContent, contentKey]);

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
    // A canvas is a command that nobody wrote: it opens the tab, for the project or the session the window has selected.
    const canvasRows = canvasCommands(canvases, title => t("Open canvas: {title}", { title }), plugin => t("A canvas of {plugin}.", { plugin }))
      .filter(command => matchesCanvasCommand(command, words))
      .map((command, index): CommandResult => ({ kind: "command", key: command.key, score: 3500 + index, name: command.name, label: command.label, description: command.description,
        group: command.group, keys: [], enabled: latest.current.canvasAvailable?.(command.item) ?? true, run: () => latest.current.onCanvas?.(command.item) }));
    const claude = query.commands || !snapshot || !claudeCode ? [] : searchClaudeCodeConversations(claudeCode.conversations, claudeCode.providerId, snapshot, words, scope);
    return {
      // The sessions of CodeAlta first; the Claude Code sessions started elsewhere follow them.
      sessions: query.commands || !snapshot ? none : [...searchSessions(snapshot, words, scope), ...claude],
      projects: query.commands || scoped || !snapshot ? none : searchProjects(snapshot, words, favorites),
      files: query.commands || scoped ? none : foundFiles.items as readonly SearchResult[],
      commands: scoped ? none : [...own, ...plugins, ...canvasRows] as readonly SearchResult[],
      // Without a query the skills are listed after a slash or in their category, not among the recent sessions.
      skills: scoped || !query.commands && !words.length && category === "all" ? none : skillSearch,
      archived: query.commands || !snapshot || !onRestore ? none
        : searchArchivedSessions(archivedProjects ? { ...snapshot, projects: [...archivedProjects] } : snapshot, archived, words, scope),
      conversations: wantContent ? foundContent.items.conversations : none,
      docs: wantContent && !scoped ? foundContent.items.docs : none,
      tools: wantContent && !scoped ? foundContent.items.tools : none,
    };
  }, [snapshot, text, scope, favorites, pluginCommands, canvases, foundFiles, foundContent, wantContent, locale, archived, archivedProjects, !onRestore, skillSearch, category, claudeCode]);
  // The archived sessions, the skills and what the index finds are categories only where there are some.
  const categories = searchCategories.filter(value => (value !== "archived" || archived.length > 0 && !!onRestore) && (value !== "skills" || offersSkills)
    && (!contentCategories.includes(value) || !!content));

  const groups = useMemo(() => scoped
    // A search kept to a project shows its sessions whole, then the conversations the index found in it.
    ? (["sessions", "conversations"] as const).filter(group => found[group].length > 0)
      .map(group => ({ group, results: found[group].slice(0, categoryLimit), total: found[group].length }))
    : shownGroups(shownCategory, found, query.commands, !words.length), [scoped, shownCategory, found, query.commands, words.length]);
  const rows = useMemo(() => flatResults(groups), [groups]);
  const index = Math.max(0, Math.min(active, rows.length - 1));
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [index, groups]);

  const enabled = (result: SearchResult) => result.kind !== "command" || result.enabled && (!result.key.startsWith("command:") || latest.current.available(result.key.slice("command:".length) as CommandId));
  function open(result: SearchResult | undefined) {
    if (!result || !enabled(result)) return;
    if (result.kind === "project") onProject(result.project);
    else if (result.kind === "session" && result.archived) onRestore?.(result.session);
    else if (result.kind === "session") onSession(result.session);
    else if (result.kind === "file") onFile(result.project, result.path);
    else if (result.kind === "skill") onSkill?.(result.skill.name, result.args);
    else if (result.kind === "content") onContent?.(result.hit);
    else if (result.kind === "claude-code") onClaudeCode?.(result.conversation, result.providerId);
    else result.run();
  }
  const move = (delta: number) => { if (rows.length) setActive((index + delta + rows.length * pageStep) % rows.length); };
  const choose = (value: SearchCategory) => { setCategory(value); setActive(0); input.current?.focus(); };
  const counts: Readonly<Record<SearchGroup, number>> = { sessions: found.sessions.length, projects: found.projects.length, files: found.files.length,
    commands: found.commands.length, skills: found.skills.length, archived: found.archived.length, conversations: found.conversations.length,
    docs: found.docs.length, tools: found.tools.length };
  const scopeName = !scoped ? null : scope === null ? t("Chats") : snapshot?.projects.find(project => project.id === scope)?.name ?? t("Unavailable project");
  const groupLabel = (group: SearchGroup) => group === "sessions" && !words.length && shownCategory === "all" ? t("Recent sessions")
    : group === "files" && files ? t("Files of {name}", { name: files.project.name }) : group === "archived" ? t("Archived sessions") : t(categoryLabels[group]);
  // Tab goes through the categories that a text starting with "/" can show, when there are skills.
  const tabCategories = query.commands ? offersSkills ? slashCategories : null : categories;
  const searching = wantFiles && (foundFiles.state === "loading" || foundFiles.state === "indexing") || wantContent && foundContent.state === "loading";
  const empty: MessageKey = shownCategory === "files" && !files ? "Open a project to search its files."
    : shownCategory === "archived" && !words.length ? "Type the name of a session."
    : shownCategory === "files" && !words.length ? "Type the name of a file."
    : shownCategory === "files" && foundFiles.state === "failed" ? "Project files could not be read."
    : contentCategories.includes(shownCategory) && !contentQuery ? "Type words to search the conversations, the guide and the tools."
    : contentCategories.includes(shownCategory) && foundContent.state === "failed" ? "The search index could not be read."
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
        : event.key === "Tab" && plain && !scoped && tabCategories ? (choose(nextCategory(shownCategory, event.shiftKey ? -1 : 1, tabCategories)), true)
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
          placeholder={t(scoped ? content && scope !== null ? "Search sessions and conversations" : "Search sessions"
            : content ? "Search sessions, conversations, files, commands and docs…" : "Search sessions, projects, files and commands…")} aria-label={t("Search")} value={text}
          onChange={event => { setText(event.target.value); setActive(0); }} />
        {searching && <ActivitySpinner size={13} />}
      </div>
      {!scoped && <div className="global-search-categories" role="tablist" aria-label={t("Search in")}>
        {categories.map(value => <button key={value} type="button" role="tab" tabIndex={-1} aria-controls="search-results" aria-selected={value === shownCategory}
          data-empty={value !== "all" && words.length > 0 && counts[value] === 0 || undefined}
          disabled={query.commands && !(offersSkills ? slashCategories : ["commands"]).includes(value)} onClick={() => choose(value)}>
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
        {/* A query that reads like a sentence and finds few conversations: search by meaning would find them by what they are about. */}
        {onOfferMeaning && wantContent && foundContent.state === "ready" && words.length >= 4 && found.conversations.length < 3
          && <p className="global-search-offer" role="note"><AppIcon name="brain" size={13} /> {t("Find conversations by what they are about.")}
            <button type="button" tabIndex={-1} onClick={onOfferMeaning}>{t("Turn on search by meaning…")}</button></p>}
      </div>
      <div className="global-search-hints">
        <span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("open")}</span>
        {!scoped && <span><kbd>Tab</kbd> {t("category")}</span>}{!scoped && <span><kbd>/</kbd> {t(offersSkills ? "commands and skills" : "commands")}</span>}
        <span className="global-search-hint-end"><kbd>Esc</kbd> {t("close")}</span>
      </div>
  </AppWindow>;
}

// The tag of a hit of the index that is no message.
const contentKindLabels: Readonly<Record<string, MessageKey>> = { note: "Notes", plan: "Plan", task: "Task", doc: "Guide", tool: "Tool", mcp: "MCP server", plugin: "Plugin" };

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
    icon = result.archived ? "archive" : result.child ? "childSession" : automated ? "automation" : session.projectId === null ? "chat" : "assistant";
    tone = result.archived ? "muted" : result.child ? "teal" : automated ? "gold" : "purple";
    const name = plainTitle(session.title);
    hint = `${name}\n${project?.name ?? t("Chat")}${result.archived ? `\n${t("Restore and open")}` : ""}`;
    title = <Marked text={name} words={words} />;
    detail = session.projectId === null ? t("Chat") : <Marked text={project?.name ?? t("Unavailable project")} words={words} />;
    const at = result.archived ? session.archivedAt ?? session.updatedAt : session.updatedAt;
    const time = sessionTime(at, locale, Math.max(now, Date.parse(at) || 0));
    meta = <>{result.archived && <span className="global-search-tag">{t("Restore")}</span>}<time dateTime={time.dateTime} title={time.title}>{time.label}</time></>;
  } else if (result.kind === "claude-code") {
    const { conversation, project } = result;
    icon = "assistant"; tone = "muted";
    const name = plainTitle(conversation.title);
    const where = project ? project.name : t("Not in a project");
    const folder = conversation.tag ?? (project ? null : conversation.startDirectory);
    hint = `${name}\n${t("From Claude Code")} · ${where}${conversation.startDirectory ? `\n${conversation.startDirectory}` : ""}`;
    title = <Marked text={name} words={words} />;
    detail = <><span className="global-search-tag">{t("From Claude Code")}</span> <Marked text={where} words={words} />
      {folder && <> · <Marked text={folder} words={words} /></>}</>;
    const time = sessionTime(conversation.lastChangedAt, locale, Math.max(now, Date.parse(conversation.lastChangedAt) || 0));
    meta = <time dateTime={time.dateTime} title={time.title}>{time.label}</time>;
  } else if (result.kind === "file") {
    const look = fileAppearance(result.path, false), { name, parent } = splitProjectPath(result.path);
    icon = look.icon; tone = look.tone; hint = result.path;
    title = <Marked text={name} words={words} />;
    detail = parent;
  } else if (result.kind === "content") {
    const { hit } = result;
    icon = contentIcon(hit); tone = hit.kind === "message" ? hit.role === "assistant" ? "purple" : "azure" : hit.kind === "doc" ? "teal" : hit.kind === "plan" || hit.kind === "task" ? "gold" : "green";
    hint = `${hit.title}\n${hit.snippet}`;
    title = <Marked text={hit.title} words={titleWords(words)} />;
    // The words the host found, as it marked them in the snippet.
    detail = <span className="global-search-snippet">{markedParts(hit.snippet, hit.marks).map((part, at) => part.marked ? <mark key={at}>{part.text}</mark> : part.text)}</span>;
    const time = hit.kind === "message" || hit.kind === "note" ? sessionTime(hit.at, locale, Math.max(now, Date.parse(hit.at) || 0)) : null;
    meta = <>{hit.match === "meaning" && <span className="global-search-tag" title={t("Related by meaning")}>≈</span>}
      {hit.kind === "message" && hit.role && <span className="global-search-tag">{t(hit.role === "assistant" ? "Answer" : hit.role === "agent" ? "Agent" : "Prompt")}</span>}
      {hit.kind !== "message" && <span className="global-search-tag">{t(contentKindLabels[hit.kind] ?? "Tool")}</span>}
      {time && <time dateTime={time.dateTime} title={time.title}>{time.label}</time>}</>;
  } else if (result.kind === "skill") {
    // A skill is named as it is invoked, with what goes with it to the session.
    const { skill, args } = result;
    icon = "skill"; tone = "green"; hint = `/${skill.name}${args ? ` ${args}` : ""}\n${skill.description}`;
    title = <><code className="global-search-skill-name">/<Marked text={skill.name} words={args ? [] : words} /></code>
      {args && <span className="global-search-skill-args">{args}</span>}</>;
    detail = skill.description;
    meta = <span className="global-search-tag">{t(skillSourceLabels[skill.source] ?? "Plugin")}</span>;
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
