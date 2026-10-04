/** The instructions of a `SKILL.md` without its front matter, whose fields the detail pane lists itself. */
export function skillInstructions(content: string): string {
  const match = /^\uFEFF?---\r?\n[\s\S]*?\r?\n---[ \t]*(?:\r?\n|$)/u.exec(content);
  return (match ? content.slice(match[0].length) : content).trim();
}
