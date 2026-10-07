import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Callout, Tab, Tabs, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon, type IconName } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { isDialogBackdrop } from "./dialogBackdrop";
import { useShellLanguage } from "./shellLanguage";
import type { TimelineItem } from "./timeline";
import type { TimelineImageSource } from "./timelineImages";
import { altaEnvelope, callDuration, changedFiles, countLines, formatDuration, formatSize, outputShape, parseArguments, parseShellResult,
  toolCallFromItem, toolCallFromRecord, toolFamily, type ToolCall, type ToolCallRecord, type ToolFamily, type ToolState } from "./toolCall";
import type { ToolCallRead, ToolCallReader } from "./toolCallReader";
import { useToolOutput, type ToolOutputs } from "./toolOutput";
import { DetailsView, EditView, GenericView, ReadView, ShellView, ToolImages } from "./ToolCallViews";

/** The icon that stands for a call: what it does when that is known, a tool otherwise. */
export function toolIcon(call: Pick<ToolCall, "name" | "kind">, family: ToolFamily): IconName {
  const name = call.name.toLowerCase();
  if (family === "shell") return "terminal";
  if (family === "edit") return "file";
  if (family === "read" || name === "read_file") return "fileCode";
  if (name === "grep" || name.includes("search")) return "search";
  if (name === "list_dir" || name === "glob" || name === "ls") return "folder";
  if (name === "view_image" || name === "take_screenshot") return "fileImage";
  if (name === "webget" || name.startsWith("web")) return "openExternal";
  if (call.kind === "Skill") return "skill";
  if (call.kind === "McpToolCall" || name.startsWith("mcp__")) return "server";
  if (call.kind === "Subagent" || call.kind === "CollabAgentToolCall") return "assistant";
  return "tool";
}

/** How far a call is, as a tag: its tile and its details say it the same way. */
export function ToolStateTag({ state }: { state: ToolState }) {
  const { t } = useShellLanguage();
  return <Tag minimal round className="tool-state" data-state={state}
    intent={state === "running" ? "primary" : state === "completed" ? "success" : state === "failed" ? "danger" : "none"}
    icon={state === "running" ? <ActivitySpinner size={11} /> : undefined}>
    {t(state === "running" ? "Running" : state === "completed" ? "Completed" : state === "failed" ? "Failed" : state === "canceled" ? "Canceled" : "Pending")}
  </Tag>;
}

// The whole record of a call. While the record of a new phase is read, the one of the previous phase stays:
// the details do not go back to what the row holds.
function useToolRecord(reader: ToolCallReader | undefined, offset: string | null, outputOffset: string | null): { record: ToolCallRecord | null; pending: boolean } {
  const key = reader && offset ? `${offset}|${outputOffset ?? ""}` : null;
  const [loaded, setLoaded] = useState<{ key: string; read: ToolCallRead } | null>(null);
  const latest = useRef<ToolCallRecord | null>(null);
  useEffect(() => {
    if (!reader || !offset || key === null) return;
    const known = reader.peek(offset, outputOffset);
    if (known) { setLoaded({ key, read: known }); return; }
    let active = true;
    void reader.read(offset, outputOffset).then(read => { if (active) setLoaded({ key, read }); },
      () => { if (active) setLoaded({ key, read: { status: "failed" } }); });
    return () => { active = false; };
  }, [reader, offset, outputOffset, key]);
  const read = key === null ? undefined : loaded?.key === key ? loaded.read : reader!.peek(offset!, outputOffset);
  if (read?.status === "ready") latest.current = read.record;
  return { record: read?.status === "ready" ? read.record : latest.current, pending: key !== null && !read };
}

function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [active]);
  return now;
}

/**
 * The details of one tool call, in a window. It follows the call: `item` is the row of the call as the timeline
 * has it now, so the window opened on a running call shows its output as it comes and its end when it ends.
 * The row is shown at once; the whole record of the call replaces it when `reader` has read it.
 */
export function ToolCallDialog({ item, reader, outputs, imageSource, current, onClose }: {
  item: TimelineItem; reader?: ToolCallReader; outputs?: ToolOutputs; imageSource?: TimelineImageSource;
  current: () => boolean; onClose: () => void }) {
  const { t } = useShellLanguage();
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  const [tab, setTab] = useState("main");
  useLayoutEffect(() => {
    const element = dialog.current!;
    if (!current()) { onClose(); return; }
    element.showModal(); closeButton.current?.focus();
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement && event.target !== element) onClose(); };
    document.addEventListener("beforetoggle", retire, true);
    return () => { document.removeEventListener("beforetoggle", retire, true); if (element.open) element.close(); };
  }, []);
  useLayoutEffect(() => { if (!current()) onClose(); });

  const identity = item.toolCall;
  const row = useMemo(() => toolCallFromItem(item), [item]);
  const { record, pending } = useToolRecord(reader, identity?.offset ?? null, identity?.outputOffset ?? null);
  const call = useMemo(() => record ? toolCallFromRecord(record, row) : row, [record, row]);
  const args = useMemo(() => parseArguments(call.arguments), [call.arguments]);
  const family = toolFamily(call);
  const ended = call.state === "completed" || call.state === "failed" || call.state === "canceled";
  // The record of an ended call has its output; until it is read, the live output stays.
  const settled = ended && !pending;
  const live = useToolOutput(outputs, identity?.activityId, !settled);
  const now = useNow(!ended && !!identity?.startedAt);
  const duration = ended ? callDuration(identity?.startedAt, identity?.endedAt)
    : identity?.startedAt ? Math.max(0, now - Date.parse(identity.startedAt)) : null;

  const shell = useMemo(() => family === "shell" && settled && call.output ? parseShellResult(call.output.text) : null, [family, settled, call.output]);
  const files = useMemo(() => family === "edit" ? changedFiles(call, args) : [], [family, call.diff, args]);
  const envelope = useMemo(() => {
    if (call.name !== "alta" || !call.output) return null;
    const shape = outputShape(call.output);
    return shape.kind === "records" ? altaEnvelope(shape.records) : null;
  }, [call.name, call.output]);
  const exitCode = call.exitCode ?? shell?.exitCode ?? envelope?.exitCode ?? null;
  const directory = call.workingDirectory ?? shell?.workingDirectory ?? null;
  const timeout = typeof args?.timeoutMs === "number" && args.timeoutMs > 0 ? args.timeoutMs : null;
  const written = family !== "shell" ? null : !settled ? live && live.total > 0 ? { lines: live.lines, bytes: live.total } : null
    : shell ? { lines: countLines(shell.stdout) + countLines(shell.stderr), bytes: shell.stdout.length + shell.stderr.length }
      : item.toolOutputLines && item.toolOutputBytes != null ? { lines: item.toolOutputLines, bytes: item.toolOutputBytes } : null;
  const added = files.reduce((sum, file) => sum + file.added, 0), removed = files.reduce((sum, file) => sum + file.removed, 0);
  // What a failed call says of its failure. A result that is only that message is not shown again under it, and
  // a message that only repeats the exit code or that the output already holds is left out.
  const failure = call.state !== "failed" ? null : call.error ?? (call.output && call.output.text.length <= 2000 ? call.output.text : null);
  const onlyFailure = failure !== null && call.output?.text.trim() === failure.trim();
  const error = failure && !(exitCode !== null && /exited with code/.test(failure)) && (onlyFailure || !call.output?.text.includes(failure)) ? failure : null;
  const shown = onlyFailure && error ? { ...call, output: null } : call;
  const images = identity?.images?.length ? <ToolImages images={identity.images} source={imageSource} /> : undefined;

  return <dialog ref={dialog} className="app-dialog tool-call-dialog" aria-labelledby={id} data-tool-family={family} data-tool-state={call.state}
    onClick={event => { if (!composing.current && isDialogBackdrop(event)) onClose(); }}
    // Effect replay can reopen the element before cleanup's queued close event arrives.
    onClose={event => { if (!event.currentTarget.open) onClose(); }} onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!event.repeat && !composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) onClose(); } }}>
    <AppWindowSurface storageKey="codealta.desktop.window.tool-call.v1" titleId={id} minimumSize={{ width: 420, height: 280 }}
      title={<span className="tool-title"><AppIcon name={toolIcon(call, family)} size={15} /><span className="tool-title-name">{call.name}</span><ToolStateTag state={call.state} /></span>}
      preferredSize={viewport => ({ width: Math.min(980, viewport.width - 40), height: Math.min(700, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={closeButton}>
      {(duration !== null || exitCode !== null || directory || timeout !== null || written || files.length > 0) && <div className="tool-summary">
        {duration !== null && <span className="tool-fact" title={t("Duration")}><AppIcon name="reminder" size={13} />{formatDuration(duration)}</span>}
        {exitCode !== null && <Tag minimal intent={exitCode === 0 ? "success" : "danger"} className="tool-exit">{t("Exit code {code}", { code: exitCode })}</Tag>}
        {written && written.bytes > 0 && <span className="tool-fact">{t("{count} lines", { count: written.lines })} · {formatSize(written.bytes)}</span>}
        {files.length > 1 && <span className="tool-fact"><AppIcon name="file" size={13} />{t("{count} files", { count: files.length })}</span>}
        {files.length > 0 && <span className="file-counts"><b>+{added}</b> <em>−{removed}</em></span>}
        {timeout !== null && <span className="tool-fact">{t("Timeout {duration}", { duration: formatDuration(timeout) })}</span>}
        {directory && <span className="tool-fact tool-fact-path" title={`${t("Working directory")}: ${directory}`}><AppIcon name="folder" size={13} /><bdi>{directory}</bdi></span>}
      </div>}
      {error && <Callout compact intent="danger" className="tool-error">{error}</Callout>}
      <Tabs id={`${id}-tabs`} className="tool-tabs" selectedTabId={tab} onChange={value => setTab(String(value))} renderActiveTabPanelOnly>
        <Tab id="main" title={t(family === "shell" ? "Output" : family === "read" ? "File" : family === "edit" ? "Changes" : "Result")} panel={<div className="tool-panel" data-view={family}>
          {family === "shell" ? <><ShellView call={shown} live={live} settled={settled} />{images}</>
            : family === "edit" ? <><EditView call={shown} args={args} />{images}</>
              : family === "read" ? <ReadView call={shown} args={args} images={images} />
                : <GenericView call={shown} args={args} images={images} />}
        </div>} />
        <Tab id="details" title={t("Details")} panel={<div className="tool-panel" data-view="details"><DetailsView item={item} call={call} duration={duration} /></div>} />
      </Tabs>
    </AppWindowSurface>
  </dialog>;
}
