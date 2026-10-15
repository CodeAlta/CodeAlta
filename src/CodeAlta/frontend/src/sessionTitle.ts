/**
 * Session titles are taken from the first prompt or answer and can start with Markdown markers. Tabs and
 * Explorer rows show them as plain text: heading, quote and list markers are dropped, and so are the bold markers
 * and backticks around text. A `**` or a backtick that marks up nothing ("tests of ** patterns") stays.
 */
export function plainTitle(title: string): string {
  const plain = title.replace(/^\s*(?:#{1,6}\s+|>\s+|[-*+]\s+)/u, "")
    .replace(/\*\*(?=\S)(.+?)(?<=\S)\*\*/gu, "$1").replace(/`([^`\n]+)`/gu, "$1")
    .replace(/\s+/gu, " ").trim();
  return plain || title;
}
