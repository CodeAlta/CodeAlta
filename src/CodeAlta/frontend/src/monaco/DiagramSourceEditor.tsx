import { CodeEditor } from "./CodeEditor";

const unchanged = () => { /* Read-only: the text is never edited. */ };

/** The Mermaid text of a diagram in its window (`DiagramSourceEditorContext`): the code editor, read-only. */
export function DiagramSourceEditor({ text, label }: { text: string; label: string }) {
  return <CodeEditor value={text} onChange={unchanged} language="plaintext" label={label} readOnly />;
}
