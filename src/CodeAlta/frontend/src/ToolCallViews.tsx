import { useEffect, useMemo, useState, type CSSProperties, type ReactNode } from "react";
import { Button, Tab, Tabs, Tag } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { DiffRows } from "./changes/DiffPreview";
import { highlightCode } from "./codeHighlight";
import { fileAppearance } from "./fileAppearance";
import { MarkdownContent } from "./MarkdownContent";
import { useShellLanguage } from "./shellLanguage";
import { timelineTime } from "./sessionTime";
import { friendly, type TimelineItem } from "./timeline";
import { useTimelineImage } from "./TimelineImageStrip";
import type { TimelineImage, TimelineImageSource } from "./timelineImages";
import { altaEnvelope, argumentEntries, changedFiles, formatDuration, highlightDiffRows, highlightLanguage, highlightLines, matchRanges, outputShape,
  parseDirectoryListing, parseNumberedLines, parseSearchMatches, parseShellResult, type ChangedFile, type DirectoryEntry, type SearchFile,
  type ShellResult, type ToolArguments, type ToolCall } from "./toolCall";
import type { ToolOutputState } from "./toolOutput";
import { ToolTerminal } from "./ToolTerminal";

/** The shell language a command of this system is written in, for its colors. */
const shellLanguage = typeof navigator !== "undefined" && navigator.platform.startsWith("Win") ? "powershell" : "bash";

/** Copies a text; the icon confirms it for a moment. */
export function CopyButton({ text, label }: { text: string; label: string }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 1600);
    return () => clearTimeout(timer);
  }, [copied]);
  return <Button variant="minimal" size="small" className="tool-copy" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
    aria-label={label} title={copied ? t("Copied") : label}
    onClick={() => void navigator.clipboard?.writeText(text).then(() => setCopied(true), () => { /* Nothing is copied. */ })} />;
}

/** A text in the colors of its language, when it has one the page knows. */
export function CodeBlock({ text, language }: { text: string; language?: string | null }) {
  const html = useMemo(() => language ? highlightCode(text, language) : null, [text, language]);
  return <pre className="tool-code" tabIndex={0}>{html !== null ? <code dangerouslySetInnerHTML={{ __html: html }} /> : <code>{text}</code>}</pre>;
}

/** Lines of a file with their numbers, in the colors of the language of the file. */
export function CodeLines({ lines, start, language }: { lines: readonly string[]; start: number; language: string }) {
  const html = useMemo(() => highlightLines(lines, language), [lines, language]);
  return <div className="tool-code-lines" role="group" tabIndex={0} style={{ "--tool-line-digits": String(start + lines.length - 1).length } as CSSProperties}>
    {lines.map((line, index) => <div key={index} className="tool-code-line"><span className="tool-code-number">{start + index}</span>
      {html ? <span className="tool-code-text" dangerouslySetInnerHTML={{ __html: html[index] || " " }} /> : <span className="tool-code-text">{line || " "}</span>}
    </div>)}
  </div>;
}

/** The command a call runs, as it is typed at a prompt. */
export function CommandBlock({ command, language = shellLanguage }: { command: string; language?: string }) {
  const { t } = useShellLanguage();
  const html = useMemo(() => highlightCode(command, language), [command, language]);
  return <div className="tool-command">
    <span className="tool-command-prompt" aria-hidden="true">❯</span>
    <pre className="tool-command-text" tabIndex={0} aria-label={t("Command")}>{html !== null ? <code dangerouslySetInnerHTML={{ __html: html }} /> : <code>{command}</code>}</pre>
    <CopyButton text={command} label={t("Copy command")} />
  </div>;
}

// A part of the details under its label. `name` marks a label that is the name of an argument, written as it is.
function Section({ label, name, action, children }: { label: string; name?: boolean; action?: ReactNode; children: ReactNode }) {
  return <section className="tool-section"><header><h3 data-name={name || undefined}>{label}</h3>{action}</header>{children}</section>;
}

/** The name of a file and its folder, with the icon of its type; `children` follow on the same line. */
export function FileHeader({ path, children }: { path: string; children?: ReactNode }) {
  const normalized = path.replaceAll("\\", "/");
  const slash = normalized.lastIndexOf("/");
  const look = fileAppearance(normalized, false);
  return <header className="tool-file-header" title={path}>
    <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
    <strong>{slash < 0 ? normalized : normalized.slice(slash + 1)}</strong>
    {slash > 0 && <small>{normalized.slice(0, slash)}</small>}
    <span className="tool-file-facts">{children}</span>
  </header>;
}

// What a shell command wrote, as one text: its standard output, then its standard error under a rule.
function shellText(result: ShellResult): string {
  if (!result.stderr) return result.stdout;
  return `${result.stdout}${result.stdout ? "\n" : ""}\x1b[2m── stderr ──\x1b[0m\n${result.stderr}`;
}

/**
 * A command and what it wrote, in a terminal. While the call runs the terminal follows its live output; once
 * its record is there (`settled`), the terminal shows the output of the record.
 */
export function ShellView({ call, live, settled }: { call: ToolCall; live: ToolOutputState | undefined; settled: boolean }) {
  const { t } = useShellLanguage();
  const result = useMemo(() => settled && call.output ? parseShellResult(call.output.text) : null, [settled, call.output]);
  const text = !settled ? live?.text ?? "" : result ? shellText(result) : call.output?.text ?? "";
  const empty = settled && !text.trim();
  return <div className="tool-shell">
    {call.command && <CommandBlock command={call.command} />}
    <div className="tool-terminal-frame">
      {empty ? <p className="tool-empty">{t("No output")}</p>
        : <ToolTerminal stream={settled ? `record:${text.length}` : "live"} text={text} offset={settled ? 0 : live?.offset ?? 0} />}
      {!settled && !text && <p className="tool-waiting">{t("Waiting for output…")}</p>}
    </div>
    {settled && call.output?.partial && <p className="tool-partial">{t("The rest is too long to show.")}</p>}
  </div>;
}

/** The lines a call read from a file, with their numbers. */
export function ReadView({ call, args, images }: { call: ToolCall; args: ToolArguments | null; images?: ReactNode }) {
  const { t } = useShellLanguage();
  const numbered = useMemo(() => call.output ? parseNumberedLines(call.output.text, call.output.partial) : null, [call.output]);
  if (!numbered) return <GenericView call={call} args={args} images={images} />;
  const path = typeof args?.path === "string" ? args.path : call.readFiles[0] ?? "";
  return <div className="tool-read">
    <FileHeader path={path}>{t("Lines {start}–{end}", { start: numbered.start, end: numbered.start + numbered.lines.length - 1 })}</FileHeader>
    <CodeLines lines={numbered.lines} start={numbered.start} language={highlightLanguage(path)} />
    {call.output?.partial && <p className="tool-partial">{t("The rest is too long to show.")}</p>}
  </div>;
}

function FileCounts({ file }: { file: ChangedFile }) {
  return <span className="file-counts"><b>+{file.added}</b> <em>−{file.removed}</em></span>;
}

function FileChange({ file }: { file: ChangedFile }) {
  const { t } = useShellLanguage();
  const path = file.movedTo ?? file.path;
  // A new file is shown as the file it is, not as a diff where every line is an addition.
  const created = file.operation === "add" && file.rows.length > 0 && file.rows.every(row => row.kind === "added" || row.kind === "hunk");
  const lines = useMemo(() => created ? file.rows.filter(row => row.kind === "added").map(row => row.text) : null, [file, created]);
  const html = useMemo(() => created ? null : highlightDiffRows(file.rows, highlightLanguage(path)), [file, path, created]);
  return <section className="tool-file">
    <FileHeader path={path}>
      {file.operation === "add" && <Tag minimal intent="success">{t("New file")}</Tag>}
      {file.operation === "delete" && <Tag minimal intent="danger">{t("Deleted")}</Tag>}
      {file.movedTo && <Tag minimal title={file.path}>{t("Moved")}</Tag>}
      <FileCounts file={file} />
    </FileHeader>
    {lines ? <CodeLines lines={lines} start={1} language={highlightLanguage(path)} />
      : file.rows.length > 0 && <div className="diff-preview" data-file-diff role="group"><DiffRows rows={file.rows} html={html!} /></div>}
  </section>;
}

/** The changes a call makes to files: the diff of one file, or a tab per file when there are several. */
export function EditView({ call, args }: { call: ToolCall; args: ToolArguments | null }) {
  const { t } = useShellLanguage();
  const files = useMemo(() => changedFiles(call, args), [call.diff, args]);
  const [selected, setSelected] = useState(0);
  if (!files.length) return <GenericView call={call} args={args} />;
  const partial = call.diff?.partial && <p className="tool-partial">{t("The rest is too long to show.")}</p>;
  if (files.length === 1) return <div className="tool-edit"><FileChange file={files[0]} />{partial}</div>;
  return <div className="tool-edit" data-files={files.length}>
    <Tabs id="tool-edit-files" vertical className="tool-edit-files" selectedTabId={Math.min(selected, files.length - 1)}
      onChange={id => setSelected(Number(id))} renderActiveTabPanelOnly>
      {files.map((file, index) => {
        const path = (file.movedTo ?? file.path).replaceAll("\\", "/");
        const look = fileAppearance(path, false);
        return <Tab key={index} id={index} disabled={false} panel={<FileChange file={file} />} title={<span className="tool-file-tab" title={path}>
          <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={13} /></span>
          <span className="tool-file-tab-name">{path.slice(path.lastIndexOf("/") + 1)}</span><FileCounts file={file} />
        </span>} />;
      })}
    </Tabs>
    {partial}
  </div>;
}

/** The images a call gave the model. */
export function ToolImages({ images, source }: { images: ReadonlyArray<TimelineImage>; source?: TimelineImageSource }) {
  return <div className="tool-images">{images.map(image => <ToolImage key={image.index} image={image} source={source} />)}</div>;
}

function ToolImage({ image, source }: { image: TimelineImage; source?: TimelineImageSource }) {
  const { t } = useShellLanguage();
  const [state] = useTimelineImage(source, image.index, true);
  return <figure className="tool-image">
    {state?.status === "ready" ? <img src={state.url} alt={image.title} />
      : <span className="tool-image-missing"><AppIcon name={state ? "imageOff" : "fileImage"} size={18} />{state ? t("Image unavailable") : ""}</span>}
    <figcaption>{image.title}</figcaption>
  </figure>;
}

// The entries of a folder, each with the icon of its type.
function DirectoryListing({ entries }: { entries: readonly DirectoryEntry[] }) {
  const { t } = useShellLanguage();
  if (!entries.length) return <p className="tool-empty">{t("Empty folder")}</p>;
  return <ul className="tool-listing">{entries.map(entry => {
    const look = fileAppearance(entry.name, entry.directory);
    return <li key={entry.name} title={entry.name}><span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span><span>{entry.name}</span></li>;
  })}</ul>;
}

// The lines a search found, by file, with what matched marked in each line.
function SearchResults({ files, pattern, caseSensitive }: { files: readonly SearchFile[]; pattern: string | null; caseSensitive: boolean }) {
  const { t } = useShellLanguage();
  if (!files.length) return <p className="tool-empty">{t("No matches")}</p>;
  return <div className="tool-search">{files.map(file => <section className="tool-search-file" key={file.path}>
    <FileHeader path={file.path}>{file.matches.length === 1 ? t("1 match") : t("{count} matches", { count: file.matches.length })}</FileHeader>
    <div className="tool-search-rows" role="group" tabIndex={0} style={{ "--tool-line-digits": String(Math.max(...file.matches.map(match => match.line))).length } as CSSProperties}>
      {file.matches.map((match, index) => {
        const parts: ReactNode[] = [];
        let end = 0;
        for (const [from, to] of pattern ? matchRanges(match.text, pattern, caseSensitive) : []) {
          if (from > end) parts.push(match.text.slice(end, from));
          parts.push(<mark key={from}>{match.text.slice(from, to)}</mark>);
          end = to;
        }
        parts.push(match.text.slice(end) || (end ? "" : " "));
        return <div className="tool-code-line" key={index}><span className="tool-code-number">{match.line}</span><span className="tool-code-text">{parts}</span></div>;
      })}
    </div>
  </section>)}</div>;
}

/** Any call: the command when it is one, its arguments by name, the images and the result. */
export function GenericView({ call, args, images }: { call: ToolCall; args: ToolArguments | null; images?: ReactNode }) {
  const { t } = useShellLanguage();
  const name = call.name.toLowerCase();
  const listing = useMemo(() => name === "list_dir" && call.output && !call.output.partial ? parseDirectoryListing(call.output.text) : null, [name, call.output]);
  const found = useMemo(() => name === "grep" && call.output && !call.output.partial ? parseSearchMatches(call.output.text) : null, [name, call.output]);
  const alta = call.name === "alta" && call.command !== null;
  const entries = useMemo(() => args ? argumentEntries(args, alta ? ["args"] : call.command !== null ? ["command"] : []) : [], [args, alta, call.command]);
  const shape = useMemo(() => call.output && call.output.text.trim() ? outputShape(call.output) : null, [call.output]);
  const records = shape?.kind === "records" ? shape.records : null;
  const shown = records && altaEnvelope(records) ? records.slice(1) : records;
  const ended = call.state === "completed" || call.state === "failed" || call.state === "canceled";
  return <div className="tool-generic">
    {call.command !== null && <CommandBlock command={call.command} language={alta ? "bash" : shellLanguage} />}
    {!args && call.arguments && call.command === null && <Section label={t("Arguments")}><CodeBlock text={call.arguments.text} /></Section>}
    {entries.some(entry => !entry.block) && <dl className="tool-arguments">{entries.filter(entry => !entry.block).map(entry =>
      <div key={entry.name}><dt>{entry.name}</dt><dd>{entry.value}</dd></div>)}</dl>}
    {entries.filter(entry => entry.block).map(entry => <Section key={entry.name} name label={entry.name} action={<CopyButton text={entry.value} label={t("Copy")} />}>
      <CodeBlock text={entry.value} language={entry.language} /></Section>)}
    {images}
    {shape && call.output && <Section label={t("Result")} action={<CopyButton text={call.output.text} label={t("Copy output")} />}>
      {listing ? <DirectoryListing entries={listing} />
        : found ? <SearchResults files={found} pattern={typeof args?.pattern === "string" ? args.pattern : null} caseSensitive={args?.caseSensitive === true} />
        : shape.kind === "text" ? <pre className="tool-output" tabIndex={0}>{shape.text}</pre>
        : shape.kind === "markdown" ? <div className="tool-markdown"><MarkdownContent source={shape.text} timelineCodeBlocks /></div>
        : shape.kind === "json" ? <CodeBlock text={shape.text} language="json" />
          : shown!.map((record, index) => <div className="tool-record" key={index}>
            {record.type && <h4>{record.type}</h4>}<CodeBlock text={record.text} language="json" /></div>)}
      {call.output.partial && <p className="tool-partial">{t("The rest is too long to show.")}</p>}
    </Section>}
    {!shape && !images && ended && call.state !== "failed" && <p className="tool-empty">{t("No output")}</p>}
  </div>;
}

/** What the records of a call hold, as they are: its identity, its arguments as JSON and the text of its result. */
export function DetailsView({ item, call, duration }: { item: TimelineItem; call: ToolCall; duration: number | null }) {
  const { t, locale } = useShellLanguage();
  const identity = item.toolCall;
  const given = useMemo(() => {
    if (!call.arguments) return null;
    if (call.arguments.partial) return { text: call.arguments.text, language: null };
    try { return { text: JSON.stringify(JSON.parse(call.arguments.text), null, 2), language: "json" }; }
    catch { return { text: call.arguments.text, language: null }; }
  }, [call.arguments]);
  const facts: [string, string | null | undefined][] = [
    [t("Tool"), call.name], [t("Kind"), friendly(call.kind)], [t("Provider"), identity?.providerId], [t("Run"), identity?.runId],
    [t("Call"), identity?.activityId],
    [t("Started"), identity?.startedAt ? timelineTime(identity.startedAt, locale).label : null],
    [t("Ended"), identity?.endedAt ? timelineTime(identity.endedAt, locale).label : null],
    [t("Duration"), duration === null ? null : formatDuration(duration)],
    [t("Working directory"), call.workingDirectory],
  ];
  return <div className="tool-details">
    <dl className="tool-facts">{facts.filter(([, value]) => value).map(([name, value]) => <div key={name}><dt>{name}</dt><dd>{value}</dd></div>)}</dl>
    {given && <Section label={t("Arguments")} action={<CopyButton text={given.text} label={t("Copy")} />}><CodeBlock text={given.text} language={given.language} /></Section>}
    {call.output && <Section label={t("Result")} action={<CopyButton text={call.output.text} label={t("Copy output")} />}>
      <pre className="tool-output" tabIndex={0}>{call.output.text}</pre></Section>}
    {call.readFiles.length > 0 && <Section label={t("Files read")}><ul className="tool-paths">{call.readFiles.map(path => <li key={path}>{path}</li>)}</ul></Section>}
    {call.modifiedFiles.length > 0 && <Section label={t("Modified files")}><ul className="tool-paths">{call.modifiedFiles.map(path => <li key={path}>{path}</li>)}</ul></Section>}
  </div>;
}
