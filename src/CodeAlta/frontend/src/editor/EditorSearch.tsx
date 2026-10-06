import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type RefObject } from "react";
import { Button, InputGroup } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import { fileAppearance, splitProjectPath } from "../fileAppearance";
import { useShellLanguage } from "../shellLanguage";
import { matchParts, searchable, searchFailure, searchMinimumLength, searchRows, type SearchMatch, type SearchOptions, type SearchResults, type SearchRow } from "./fileSearch";
import { useWindowedRows } from "./windowedRows";

export type SearchHandle = Readonly<{ focus: () => void }>;
const rowHeight = 22;

const FileRow = memo(function FileRow({ row, selected, onToggle }: { row: Extract<SearchRow, { kind: "file" }>; selected: boolean; onToggle: (path: string) => void }) {
  const look = fileAppearance(row.file.path, false), { name, parent } = splitProjectPath(row.file.path);
  return <div role="treeitem" aria-level={1} aria-expanded={!row.collapsed} aria-selected={selected} className="editor-search-row editor-search-file" data-key={row.key}
    title={row.file.path} onClick={() => onToggle(row.file.path)}>
    <AppIcon name={row.collapsed ? "chevronRight" : "chevronDown"} size={13} />
    <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
    <span className="editor-search-name"><strong>{name}</strong>{parent && <small>{parent}</small>}</span>
    <span className="editor-search-count">{row.file.matches.length}{row.file.truncated ? "+" : ""}</span>
  </div>;
});

const MatchRow = memo(function MatchRow({ row, selected, onOpen }: {
  row: Extract<SearchRow, { kind: "match" }>; selected: boolean; onOpen: (path: string, match: SearchMatch, keep: boolean) => void;
}) {
  const [before, hit, after] = matchParts(row.match);
  return <div role="treeitem" aria-level={2} aria-selected={selected} className="editor-search-row editor-search-match" data-key={row.key}
    title={`${row.path}:${row.match.line}`} onClick={() => onOpen(row.path, row.match, false)} onDoubleClick={() => onOpen(row.path, row.match, true)}>
    <span className="editor-search-line">{row.match.line}</span>
    <span className="editor-search-text">{before}<mark>{hit}</mark>{after}</span>
  </div>;
});

function Toggle({ icon, label, active, onChange }: { icon: IconName; label: string; active: boolean; onChange: (value: boolean) => void }) {
  return <Button variant="minimal" size="small" className="editor-search-toggle" icon={<AppIcon name={icon} size={15} />} active={active} aria-pressed={active}
    aria-label={label} title={label} onClick={() => onChange(!active)} />;
}

/**
 * The search through the files of a project: a text or a regular expression, with the files to look in and to
 * leave out, and the matches of each file as they are found. A match opens its file on its line.
 */
export function EditorSearch({ options, results, handle, onOptions, onSubmit, onOpen }: {
  options: SearchOptions; results: SearchResults | null;
  handle: RefObject<SearchHandle | null>;
  onOptions: (change: Partial<SearchOptions>) => void;
  /** Enter in the search field: search now instead of after the typing pause. */
  onSubmit: () => void;
  /** Opens a file at a match: as a preview, or kept open with the keyboard in the editor. */
  onOpen: (path: string, match: SearchMatch, keep: boolean) => void;
}) {
  const { t, locale } = useShellLanguage();
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const [details, setDetails] = useState(() => options.include !== "" || options.exclude !== "");
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set());
  const [selected, setSelected] = useState<string | null>(null);
  const rows = useMemo(() => searchRows(results, collapsed), [results, collapsed]);
  const windowed = useWindowedRows(list, rows.length, rowHeight);
  useEffect(() => { handle.current = { focus: () => { input.current?.focus(); input.current?.select(); } }; return () => { handle.current = null; }; }, [handle]);
  useEffect(() => { setCollapsed(new Set()); setSelected(null); }, [results?.key]);
  const selectedIndex = rows.findIndex(row => row.key === selected);
  useLayoutEffect(() => { if (selectedIndex >= 0) windowed.reveal(selectedIndex); }, [selectedIndex]);

  const toggle = useRef((path: string) => {
    setSelected(path);
    setCollapsed(current => { const next = new Set(current); if (!next.delete(path)) next.add(path); return next; });
  });
  const latest = useRef({ onOpen, rows }); latest.current = { onOpen, rows };
  const open = useRef((path: string, match: SearchMatch, keep: boolean) => {
    const row = latest.current.rows.find(value => value.kind === "match" && value.path === path && value.match === match);
    if (row) setSelected(row.key);
    latest.current.onOpen(path, match, keep);
  });

  function listKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.nativeEvent.isComposing || !rows.length) return;
    const at = selectedIndex, row = at >= 0 ? rows[at] : undefined;
    const move = (index: number) => setSelected(rows[Math.max(0, Math.min(rows.length - 1, index))].key);
    if (event.key === "ArrowDown") move(at + 1);
    else if (event.key === "ArrowUp") { if (at <= 0) input.current?.focus(); else move(at - 1); }
    else if (event.key === "Home") move(0);
    else if (event.key === "End") move(rows.length - 1);
    else if (event.key === "PageDown") move(Math.max(at, 0) + 14);
    else if (event.key === "PageUp") move(Math.max(at, 0) - 14);
    else if (row?.kind === "file" && (event.key === "Enter" || event.key === " " || event.key === (row.collapsed ? "ArrowRight" : "ArrowLeft"))) toggle.current(row.file.path);
    else if (row?.kind === "match" && (event.key === "Enter" || event.key === " ")) onOpen(row.path, row.match, event.key === "Enter");
    else if (row?.kind === "match" && event.key === "ArrowLeft") setSelected(row.path);
    else return;
    event.preventDefault();
  }

  const ready = searchable(options);
  const files = results?.files.length ?? 0, matches = results?.matches ?? 0;
  const count = (value: number) => value.toLocaleString(locale);
  const summary = !ready ? (options.query ? t("Type at least {count} characters.", { count: searchMinimumLength }) : null)
    : !results ? null
    : results.state === "failed" ? t(searchFailure(results.status))
    : results.state === "searching" ? (matches ? t("Searching… {count} found", { count: count(matches) }) : t("Searching…"))
    : matches === 0 ? t("No results.")
    : matches === 1 ? t("1 result in 1 file")
    : files === 1 ? t("{count} results in 1 file", { count: count(matches) })
    : t("{count} results in {files} files", { count: count(matches), files: count(files) });
  return <section className="editor-search" aria-label={t("Search in files")}>
    <div className="editor-search-form">
      <InputGroup inputRef={input} className="editor-search-query" size="small" value={options.query} maxLength={1024} spellCheck={false} autoComplete="off"
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} placeholder={t("Search in files")} aria-label={t("Search in files")}
        aria-invalid={results?.state === "failed" && results.status === "invalid_pattern"}
        onValueChange={query => onOptions({ query })}
        onKeyDown={event => {
          if (event.nativeEvent.isComposing) return;
          if (event.key === "Enter") { event.preventDefault(); onSubmit(); }
          else if (event.key === "ArrowDown" && rows.length) { event.preventDefault(); list.current?.focus(); setSelected(current => current ?? rows[0].key); }
        }}
        rightElement={<span className="editor-search-toggles">
          <Toggle icon="caseSensitive" label={t("Match case")} active={options.matchCase} onChange={matchCase => onOptions({ matchCase })} />
          <Toggle icon="wholeWord" label={t("Match whole word")} active={options.wholeWord} onChange={wholeWord => onOptions({ wholeWord })} />
          <Toggle icon="regex" label={t("Use regular expression")} active={options.regex} onChange={regex => onOptions({ regex })} />
        </span>} />
      <Button variant="minimal" size="small" icon={<AppIcon name="filter" size={14} />} active={details} aria-pressed={details} aria-expanded={details}
        aria-label={t("Files to include and exclude")} title={t("Files to include and exclude")} onClick={() => setDetails(value => !value)} />
    </div>
    {details && <div className="editor-search-globs">
      <label><span>{t("Files to include")}</span>
        <InputGroup size="small" value={options.include} maxLength={1024} spellCheck={false} autoComplete="off" placeholder="src, *.ts"
          aria-invalid={results?.state === "failed" && results.status === "invalid_glob"} onValueChange={include => onOptions({ include })}
          onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); onSubmit(); } }} /></label>
      <label><span>{t("Files to exclude")}</span>
        <InputGroup size="small" value={options.exclude} maxLength={1024} spellCheck={false} autoComplete="off" placeholder="*.min.js, dist"
          onValueChange={exclude => onOptions({ exclude })} onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); onSubmit(); } }} /></label>
    </div>}
    {summary && <p className="editor-search-summary" role="status" data-state={results?.state === "failed" ? "failed" : undefined}>
      {ready && results?.state === "searching" && <ActivitySpinner size={12} />}<span>{summary}</span></p>}
    {results?.state === "done" && results.truncated && <p className="editor-search-summary editor-search-more">{t("Only the first results are shown. Narrow the search to see the others.")}</p>}
    <div ref={list} className="editor-search-results" role="tree" tabIndex={rows.length ? 0 : -1} aria-label={t("Search results")} onScroll={windowed.onScroll} onKeyDown={listKeyDown}>
      <div style={{ height: windowed.before }} />
      {rows.slice(windowed.first, windowed.last).map(row => row.kind === "file"
        ? <FileRow key={row.key} row={row} selected={row.key === selected} onToggle={toggle.current} />
        : <MatchRow key={row.key} row={row} selected={row.key === selected} onOpen={open.current} />)}
      <div style={{ height: windowed.after }} />
    </div>
  </section>;
}
