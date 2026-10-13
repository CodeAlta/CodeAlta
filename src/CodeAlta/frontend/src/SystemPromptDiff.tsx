import { useRef, useState, type ReactNode } from "react";
import { Button, ButtonGroup } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { DiffEditor, type DiffEditorChanges, type DiffEditorHandle } from "./monaco/DiffEditor";
import { useShellLanguage } from "./shellLanguage";

/** How the comparison of two prompts is shown. */
export type PromptDiffLook = Readonly<{ sideBySide: boolean; wrap: boolean; collapseUnchanged: boolean }>;

/** What the comparison of two prompts is given. */
export type PromptDiffProps = Readonly<{
  /** Identifies the compared prompt: another one starts at its first change. */
  documentKey: string; original: string; modified: string;
  look: PromptDiffLook; onLook: (change: Partial<PromptDiffLook>) => void;
  /** What the header says of the change, before its controls. */
  summary: ReactNode;
}>;

/**
 * A prompt against the one it replaced, as Markdown: the controls of the diff of a file in the Changes tab, as
 * far as they make sense for one text. It brings the editor with it: the application gives it to the window of
 * a prompt (`SystemPromptDiffContext`), which does not depend on the editor itself.
 */
export function SystemPromptDiff({ documentKey, original, modified, look, onLook, summary }: PromptDiffProps) {
  const { t } = useShellLanguage();
  const [changes, setChanges] = useState<DiffEditorChanges>({ count: -1, current: 0 });
  const diff = useRef<DiffEditorHandle | null>(null);
  return <div className="prompt-diff"
    onKeyDown={event => {
      if (!event.altKey || event.ctrlKey || event.metaKey || event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
      event.preventDefault();
      diff.current?.go(event.key === "ArrowUp" ? "previous" : "next");
    }}>
    <div className="changes-diff-header prompt-diff-header">
      {summary}
      <span className="changes-spacer" />
      <span className="changes-navigation">
        {changes.count >= 0 && <span className="changes-position">{changes.count === 0 ? t("No difference") : changes.current > 0
          ? t("{current} of {count}", { current: changes.current, count: changes.count })
          : t(changes.count === 1 ? "{count} change" : "{count} changes", { count: changes.count })}</span>}
        <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={14} />} disabled={changes.count <= 0}
          aria-label={t("Previous change")} title={`${t("Previous change")} (Alt+↑)`} onClick={() => diff.current?.go("previous")} />
        <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={14} />} disabled={changes.count <= 0}
          aria-label={t("Next change")} title={`${t("Next change")} (Alt+↓)`} onClick={() => diff.current?.go("next")} />
      </span>
      <ButtonGroup className="changes-view">
        <Button variant="minimal" size="small" active={look.sideBySide} icon={<AppIcon name="columns" size={14} />} aria-pressed={look.sideBySide}
          aria-label={t("Side by side")} title={t("Side by side")} onClick={() => onLook({ sideBySide: true })} />
        <Button variant="minimal" size="small" active={!look.sideBySide} icon={<AppIcon name="rows" size={14} />} aria-pressed={!look.sideBySide}
          aria-label={t("Inline")} title={t("Inline")} onClick={() => onLook({ sideBySide: false })} />
      </ButtonGroup>
      <Button variant="minimal" size="small" active={look.collapseUnchanged} icon={<AppIcon name="fold" size={14} />} aria-pressed={look.collapseUnchanged}
        aria-label={t("Hide unchanged lines")} title={t("Hide unchanged lines")} onClick={() => onLook({ collapseUnchanged: !look.collapseUnchanged })} />
      <Button variant="minimal" size="small" active={look.wrap} icon={<AppIcon name="wrap" size={14} />} aria-pressed={look.wrap}
        aria-label={t("Wrap lines")} title={t("Wrap lines")} onClick={() => onLook({ wrap: !look.wrap })} />
    </div>
    <div className="changes-diff-surface">
      <DiffEditor documentKey={documentKey} original={original} modified={modified} language="markdown" label={t("Changes")}
        sideBySide={look.sideBySide} wrap={look.wrap} ignoreWhitespace={false} collapseUnchanged={look.collapseUnchanged} onChanges={setChanges} handle={diff} />
    </div>
  </div>;
}
