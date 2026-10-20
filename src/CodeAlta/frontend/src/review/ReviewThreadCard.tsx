import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode, type Ref } from "react";
import { Button, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { MarkdownContent } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { suggestionDiff, threadChip, wholeFile, type ReviewChip, type ReviewThread } from "./reviewThreads";

/** The longest comment, reply or suggestion the host keeps, in UTF-16 units. */
export const maximumReviewText = 16384;

/** What a card can do with its thread; each resolves to whether the host did it. */
export type ReviewThreadActions = Readonly<{
  update: (id: string, body: string, suggestion: string | null) => Promise<boolean>;
  remove: (id: string) => Promise<boolean>;
  resolve: (id: string, resolved: boolean) => Promise<boolean>;
  reply: (id: string, body: string) => Promise<boolean>;
}>;

type Translate = ReturnType<typeof useShellLanguage>["t"];
type Lines = Readonly<{ startLine: number; endLine: number }>;

const chipLabels: Readonly<Record<ReviewChip, MessageKey>> = { draft: "draft", sent: "sent", addressed: "addressed", resolved: "resolved", outdated: "outdated" };

/** "line 5", "lines 15–18" or "whole file". */
export function linesLabel(t: Translate, lines: Lines): string {
  if (wholeFile(lines)) return t("whole file");
  return lines.startLine === lines.endLine ? t("line {line}", { line: lines.startLine }) : t("lines {start}–{end}", { start: lines.startLine, end: lines.endLine });
}

/** What a screen reader says of a comment: "Comment on lines 15 to 18 of PriceService.cs". */
export function threadLabel(t: Translate, lines: Lines, path: string): string {
  const file = path.slice(path.lastIndexOf("/") + 1);
  if (wholeFile(lines)) return t("Comment on {file}", { file });
  return lines.startLine === lines.endLine ? t("Comment on line {line} of {file}", { line: lines.startLine, file })
    : t("Comment on lines {start} to {end} of {file}", { start: lines.startLine, end: lines.endLine, file });
}

/** The text a suggestion replaces, shown as a small diff: the lines taken out, then the ones put in. */
export function SuggestionDiff({ original, suggestion }: { original: string; suggestion: string }) {
  const { t } = useShellLanguage();
  const lines = suggestionDiff(original, suggestion);
  return <figure className="review-suggestion" aria-label={t(suggestion === "" ? "Suggestion: delete these lines" : "Suggested change")}>
    <figcaption><AppIcon name="edit" size={12} />{t(suggestion === "" ? "Suggestion: delete these lines" : "Suggested change")}</figcaption>
    <pre>{lines.map((line, index) => <span key={index} className="review-suggestion-line" data-kind={line.kind}>
      <span className="review-suggestion-mark" aria-hidden="true">{line.kind === "removed" ? "−" : line.kind === "added" ? "+" : " "}</span>{line.text || " "}{"\n"}</span>)}</pre>
  </figure>;
}

/** Ctrl+Enter (Cmd+Enter on macOS) saves; Esc cancels. Typing that composes a character is left alone. */
function editorKey(event: ReactKeyboardEvent, save: () => void, cancel: () => void) {
  if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey) { event.preventDefault(); event.stopPropagation(); save(); }
  else if (event.key === "Escape" && !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey) { event.preventDefault(); event.stopPropagation(); cancel(); }
}

/** What the buttons of an editor's header can do: start a suggestion from the lines, or save at once. */
export type ReviewEditorControls = Readonly<{ suggest: () => void; save: (suggestion: string | null) => void; saving: boolean }>;

/**
 * The editor of a comment: its text and, after **Suggest**, the text it proposes in place of its lines, prefilled
 * with those lines. `Ctrl+Enter` saves, `Esc` cancels. `actions` adds buttons to its header (Suggest edit, Delete block).
 */
export function ReviewCommentEditor({ label, lines, original, initialBody = "", initialSuggestion = null, submitText, actions, suggestButton = true, onSave, onCancel, editorRef }: {
  /** What a screen reader says of the editor. */
  label: string;
  lines: Lines;
  /** The text of the lines, as it is now: the start of a suggestion. */
  original: string;
  initialBody?: string;
  initialSuggestion?: string | null;
  submitText?: string;
  actions?: (controls: ReviewEditorControls) => ReactNode;
  /** Whether the footer has **Suggest** (the header's actions may offer it instead). */
  suggestButton?: boolean;
  /** Saves the comment; false keeps the editor open, with what the user wrote. */
  onSave: (body: string, suggestion: string | null) => Promise<boolean>;
  onCancel: () => void;
  editorRef?: Ref<HTMLTextAreaElement>;
}) {
  const { t } = useShellLanguage();
  const [body, setBody] = useState(initialBody);
  const [suggestion, setSuggestion] = useState<string | null>(initialSuggestion);
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const suggestionBox = useRef<HTMLTextAreaElement | null>(null);
  const empty = body.trim() === "" && suggestion === null;
  async function save(chosen = suggestion) {
    if (saving || body.trim() === "" && chosen === null) return;
    setSaving(true); setFailed(false);
    const done = await onSave(body, chosen).catch(() => false);
    if (!alive.current) return;
    setSaving(false);
    if (!done) setFailed(true);
  }
  const keys = (event: ReactKeyboardEvent) => editorKey(event, () => void save(), onCancel);
  const suggest = () => { if (suggestion === null) setSuggestion(original); requestAnimationFrame(() => suggestionBox.current?.focus()); };
  return <div className="review-editor" role="group" aria-label={label} onKeyDown={keys}>
    <header className="review-card-head">
      <AppIcon name="user" size={13} /><strong>{t("You")}</strong><span>{linesLabel(t, lines)}</span>
      <span className="review-card-actions">{actions?.({ suggest, save: chosen => void save(chosen), saving })}</span>
    </header>
    <TextArea fill autoResize inputRef={editorRef} autoFocus maxLength={maximumReviewText} value={body} disabled={saving} aria-label={label}
      placeholder={t("Leave a comment… Ctrl+Enter saves · Esc cancels")} onChange={event => setBody(event.target.value)} />
    {suggestion !== null && <div className="review-editor-suggestion">
      <span className="review-editor-suggestion-head"><AppIcon name="edit" size={12} />{t("Suggested text")}
        <Button size="small" variant="minimal" icon={<AppIcon name="close" size={12} />} aria-label={t("Remove the suggestion")} title={t("Remove the suggestion")}
          disabled={saving} onClick={() => setSuggestion(null)} /></span>
      <TextArea fill autoResize inputRef={suggestionBox} className="review-editor-code" maxLength={maximumReviewText} value={suggestion} disabled={saving}
        aria-label={t("Suggested text")} spellCheck={false} onChange={event => setSuggestion(event.target.value)} />
    </div>}
    {failed && <p className="review-card-error" role="alert">{t("The comment could not be saved.")}</p>}
    <footer className="review-editor-footer">
      {suggestButton && suggestion === null && !wholeFile(lines) && <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={13} />} disabled={saving}
        title={t("Propose a replacement for these lines")} onClick={suggest}>{t("Suggest")}</Button>}
      <span className="review-editor-spacer" />
      <Button size="small" variant="minimal" disabled={saving} onClick={onCancel}>{t("Cancel")}</Button>
      <Button size="small" intent="primary" loading={saving} disabled={empty} title="Ctrl+Enter" onClick={() => void save()}>{submitText ?? t("Comment")}</Button>
    </footer>
  </div>;
}

/**
 * A thread of a review: the comment of the user, its suggestion as a small diff, and the replies, with what can be
 * done with it in its state. A draft can be edited and deleted; a thread sent to the agent can be answered and
 * resolved; a resolved one opened again. The card takes the keyboard (it is focusable, and named for screen readers),
 * and `Esc` gives it back to the line it is about.
 */
export function ReviewThreadCard({ thread, original, actions, onLeave, cardRef, readOnly = false }: {
  thread: ReviewThread;
  /** The text of the thread's lines as they are now, for a suggestion; the anchor when it is outdated. */
  original?: string;
  /** Null shows the thread without its actions. */
  actions: ReviewThreadActions | null;
  /** `Esc` on the card: back to its line. */
  onLeave?: () => void;
  cardRef?: Ref<HTMLElement>;
  /** The review cannot be changed now (a request being answered, a file being edited). */
  readOnly?: boolean;
}) {
  const { t, locale } = useShellLanguage();
  const [mode, setMode] = useState<"view" | "edit" | "reply" | "delete">("view");
  const [reply, setReply] = useState("");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const chip = threadChip(thread);
  const able = !!actions && !readOnly && !busy;
  const lines = { startLine: thread.startLine, endLine: thread.endLine };
  const before = thread.outdated ? thread.anchor : original ?? thread.anchor;
  async function act(run: () => Promise<boolean>, after: "view" | null = "view") {
    setBusy(true); setFailed(false);
    const done = await run().catch(() => false);
    if (!alive.current) return;
    setBusy(false);
    if (!done) setFailed(true); else if (after) setMode(after);
  }
  const at = (value: string) => { const date = new Date(value); return Number.isNaN(date.getTime()) ? "" : date.toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" }); };
  return <article className="review-thread" data-state={thread.state} data-chip={chip} tabIndex={0} ref={cardRef} data-thread-id={thread.id}
    aria-label={`${threadLabel(t, lines, thread.path)} · ${t(chipLabels[chip])}`}
    onKeyDown={event => {
      if (event.defaultPrevented || event.target !== event.currentTarget) return;
      if (event.key === "Escape" && !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey && onLeave) { event.preventDefault(); event.stopPropagation(); onLeave(); }
    }}>
    {mode === "edit" && actions ? <ReviewCommentEditor label={threadLabel(t, lines, thread.path)} lines={lines} original={before} initialBody={thread.body}
      initialSuggestion={thread.suggestion} submitText={t("Save")} onCancel={() => setMode("view")}
      onSave={async (body, suggestion) => { const done = await actions.update(thread.id, body, suggestion); if (done && alive.current) setMode("view"); return done; }} />
    : <>
      <header className="review-card-head">
        <AppIcon name="user" size={13} /><strong>{t("You")}</strong><span>{linesLabel(t, lines)}{thread.side === "old" && thread.target !== "file" ? ` (${t("old")})` : ""}</span>
        <span className="review-chip" data-chip={chip}>{t(chipLabels[chip])}</span>
        {actions && <span className="review-card-actions">
          {mode === "delete" ? <>
            <Button size="small" intent="danger" disabled={!able} onClick={() => void act(() => actions.remove(thread.id), null)}>{t("Delete")}</Button>
            <Button size="small" variant="minimal" disabled={busy} onClick={() => setMode("view")}>{t("Keep")}</Button>
          </> : <>
            {(thread.state === "draft" || thread.state === "open") && <Button size="small" variant="minimal" disabled={!able} onClick={() => setMode("edit")}>{t("Edit")}</Button>}
            {thread.state === "draft" && <Button size="small" variant="minimal" disabled={!able} onClick={() => setMode("delete")}>{t("Delete")}</Button>}
            {(thread.state === "open" || thread.state === "addressed") && <Button size="small" variant="minimal" disabled={!able} onClick={() => setMode("reply")}>{t("Reply")}</Button>}
            {(thread.state === "open" || thread.state === "addressed") && <Button size="small" variant="minimal" icon={<AppIcon name="check" size={12} />} disabled={!able}
              onClick={() => void act(() => actions.resolve(thread.id, true))}>{t("Resolve")}</Button>}
            {thread.state === "resolved" && <Button size="small" variant="minimal" icon={<AppIcon name="restore" size={12} />} disabled={!able}
              onClick={() => void act(() => actions.resolve(thread.id, false))}>{t("Reopen")}</Button>}
          </>}
        </span>}
      </header>
      {thread.outdated && thread.anchor && <blockquote className="review-anchor" aria-label={t("The text this comment was about")}><pre>{thread.anchor}</pre></blockquote>}
      {thread.body.trim() && <div className="review-card-body"><MarkdownContent source={thread.body} /></div>}
      {thread.suggestion !== null && <SuggestionDiff original={before} suggestion={thread.suggestion} />}
      {thread.replies.length > 0 && <ol className="review-replies">{thread.replies.map((item, index) => <li key={index} data-author={item.author}>
        <header className="review-card-head"><AppIcon name={item.author === "agent" ? "assistant" : "user"} size={13} />
          <strong>{t(item.author === "agent" ? "Agent" : "You")}</strong><span title={item.at}>{at(item.at)}</span></header>
        <div className="review-card-body"><MarkdownContent source={item.body} /></div>
      </li>)}</ol>}
      {mode === "reply" && actions && <div className="review-reply" onKeyDown={event => editorKey(event, () => {
        if (reply.trim()) void act(async () => { const done = await actions.reply(thread.id, reply); if (done && alive.current) setReply(""); return done; });
      }, () => setMode("view"))}>
        <TextArea fill autoResize autoFocus maxLength={maximumReviewText} value={reply} disabled={busy} aria-label={t("Reply")}
          placeholder={t("Reply… Ctrl+Enter sends · Esc cancels")} onChange={event => setReply(event.target.value)} />
        <footer className="review-editor-footer"><span className="review-editor-spacer" />
          <Button size="small" variant="minimal" disabled={busy} onClick={() => setMode("view")}>{t("Cancel")}</Button>
          <Button size="small" intent="primary" loading={busy} disabled={!reply.trim()} title="Ctrl+Enter"
            onClick={() => void act(async () => { const done = await actions.reply(thread.id, reply); if (done && alive.current) setReply(""); return done; })}>{t("Reply")}</Button>
        </footer>
      </div>}
      {failed && <p className="review-card-error" role="alert">{t("The review could not be changed.")}</p>}
    </>}
  </article>;
}
