/**
 * Session titles are taken from the first prompt or answer and can start with Markdown markers. Tabs and
 * Explorer rows show them as plain text: heading, quote and list markers, bold markers and backticks are dropped.
 */
export function plainTitle(title: string): string {
  const plain = title.replace(/^\s*(?:#{1,6}\s+|>\s+|[-*+]\s+)/u, "").replace(/\*\*|`/gu, "").replace(/\s+/gu, " ").trim();
  return plain || title;
}
