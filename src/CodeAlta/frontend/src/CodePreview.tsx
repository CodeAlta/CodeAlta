// React text nodes only: JSON values never become markup, Markdown, or links.
export function CodePreview({ text, field }: { text: string; field?: string }) {
  let source = text;
  let json = false;
  try { source = JSON.stringify(JSON.parse(text), null, 2); json = true; } catch { /* Incomplete JSON is literal code. */ }
  const tokens = json ? source.split(/("(?:\\.|[^"\\])*"\s*:|"(?:\\.|[^"\\])*"|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g) : [source];
  return <pre className="code-preview" data-tool-field={field}><code>{tokens.map((token, index) => {
    const kind = !json ? "" : token.startsWith('"') ? token.trimEnd().endsWith(":") ? "key" : "string"
      : /^(true|false|null)$/.test(token) ? "literal" : /^-?\d/.test(token) ? "number" : "";
    return kind ? <span key={index} className={`json-${kind}`}>{token}</span> : token;
  })}</code></pre>;
}
