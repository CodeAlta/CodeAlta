import type { ReactNode, Ref } from "react";

/** The page of the Markdown preview of a file. It takes the keyboard, as the system scrolls what has it: the arrows, Page Up and Page Down, Home and End. */
export function MarkdownPreviewBox({ label, review, boxRef, children }: { label: string; review: boolean; boxRef?: Ref<HTMLDivElement>; children: ReactNode }) {
  return <div className="editor-markdown" role="region" aria-label={label} data-review={review || undefined} ref={boxRef} tabIndex={0}>{children}</div>;
}
