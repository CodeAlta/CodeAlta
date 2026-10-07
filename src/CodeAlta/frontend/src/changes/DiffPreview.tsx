import { useMemo } from "react";
import { AppIcon } from "../AppIcon";
import { fileAppearance } from "../fileAppearance";
import { highlightDiffRows, highlightLanguage } from "../toolCall";
import { parseUnifiedDiff, type DiffLine } from "./unifiedDiff";

// The text of a line becomes markup only through the highlighter, which writes escaped text in `span`
// elements: a line of a diff is never a link or active content.
/**
 * The rows of a diff as colored lines with the line numbers of both sides and a rule per hunk. `html` gives the
 * highlighted text of the rows that have one (see `highlightDiffRows`); the others are shown as they are.
 */
export function DiffRows({ rows, html }: { rows: readonly DiffLine[]; html?: readonly (string | null)[] }) {
  return <>{rows.map((row, index) => {
    if (row.kind === "file") {
      const look = fileAppearance(row.text, false);
      return <div key={index} className="diff-preview-file"><span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={13} /></span>{row.text}</div>;
    }
    if (row.kind === "hunk") return <div key={index} className="diff-preview-hunk"><span>{row.text || "⋯"}</span></div>;
    const highlighted = html?.[index];
    return <div key={index} className="diff-preview-line" data-kind={row.kind}>
      <span className="diff-preview-number">{row.oldLine ?? ""}</span><span className="diff-preview-number">{row.newLine ?? ""}</span>
      <span className="diff-preview-sign">{row.kind === "added" ? "+" : row.kind === "removed" ? "−" : ""}</span>
      {highlighted ? <span className="diff-preview-text" dangerouslySetInnerHTML={{ __html: highlighted }} />
        : <span className="diff-preview-text">{row.text || " "}</span>}
    </div>;
  })}</>;
}

// The highlighted text of the rows of a diff of several files: each file in its own language. `language` is
// the one of the rows that no file header precedes.
function highlightFiles(rows: readonly DiffLine[], language: string): (string | null)[] {
  const html: (string | null)[] = rows.map(() => null);
  let start = 0;
  const flush = (end: number) => {
    if (end > start) highlightDiffRows(rows.slice(start, end), language).forEach((value, index) => { html[start + index] = value; });
  };
  rows.forEach((row, index) => {
    if (row.kind !== "file") return;
    flush(index);
    start = index + 1;
    language = highlightLanguage(row.text);
  });
  flush(rows.length);
  return html;
}

/**
 * A unified diff as colored rows with the line numbers of both sides, a header per file and a rule per hunk.
 * `path` names the file of a diff that has no file header, for the colors of its language.
 */
export function DiffPreview({ text, path }: { text: string; path?: string }) {
  const rows = useMemo(() => parseUnifiedDiff(text), [text]);
  const html = useMemo(() => highlightFiles(rows, path ? highlightLanguage(path) : "plaintext"), [rows, path]);
  return <div className="diff-preview" data-file-diff role="group"><DiffRows rows={rows} html={html} /></div>;
}
