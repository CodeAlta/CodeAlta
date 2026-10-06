import { useMemo } from "react";
import { AppIcon } from "../AppIcon";
import { fileAppearance } from "../fileAppearance";
import { parseUnifiedDiff } from "./unifiedDiff";

// React text nodes only: the lines of a diff never become markup or links.
/** A unified diff as colored rows with the line numbers of both sides, a header per file and a rule per hunk. */
export function DiffPreview({ text }: { text: string }) {
  const rows = useMemo(() => parseUnifiedDiff(text), [text]);
  return <div className="diff-preview" data-file-diff role="group">{rows.map((row, index) => {
    if (row.kind === "file") {
      const look = fileAppearance(row.text, false);
      return <div key={index} className="diff-preview-file"><span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={13} /></span>{row.text}</div>;
    }
    if (row.kind === "hunk") return <div key={index} className="diff-preview-hunk"><span>{row.text || "⋯"}</span></div>;
    return <div key={index} className="diff-preview-line" data-kind={row.kind}>
      <span className="diff-preview-number">{row.oldLine ?? ""}</span><span className="diff-preview-number">{row.newLine ?? ""}</span>
      <span className="diff-preview-sign">{row.kind === "added" ? "+" : row.kind === "removed" ? "−" : ""}</span>
      <span className="diff-preview-text">{row.text || " "}</span>
    </div>;
  })}</div>;
}
