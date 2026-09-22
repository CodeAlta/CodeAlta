import DOMPurify from "dompurify";
import { useMemo } from "react";
import { renderMarkdownHtml } from "./markdown";

export function MarkdownContent({ source }: { source: string }) {
  const html = useMemo(() => DOMPurify.sanitize(renderMarkdownHtml(source), {
    FORBID_ATTR: ["style"],
    FORBID_TAGS: ["button", "form", "input", "option", "select", "style", "textarea"],
  }), [source]);

  return <div className="markdown-content" dangerouslySetInnerHTML={{ __html: html }} />;
}
