// The words of an activity line, from text alone: what the desktop timeline and the phone page (src/mobile) both show.
// No import here: the phone page uses this module and must not reach the desktop's RPC client.

const plainLine = (line: string) => line.replace(/^#+\s*/u, "").replace(/^[-*>]\s+/u, "").replace(/\*\*|__|`/gu, "").replace(/\s+/gu, " ").trim();

/**
 * What a thought says of the work, in one line of plain text. A reasoning summary (Codex, Copilot) starts with a
 * title in bold, which is the line. A thought written as prose (Claude) says last what it is about to do: the line
 * is its last sentence. Markdown marks are left out.
 */
export function thoughtHeadline(markdown: string | null | undefined): string | null {
  if (!markdown) return null;
  const text = markdown.replaceAll("\r\n", "\n").trim();
  const title = /^\*\*([^*\n]+)\*\*/u.exec(text);
  if (title) return plainLine(title[1]) || null;
  // The last paragraph that is prose, not code.
  const paragraphs = text.replace(/```[\s\S]*?(```|$)/gu, "").split(/\n\s*\n/u).map(paragraph => plainLine(paragraph.split("\n").map(plainLine).join(" "))).filter(Boolean);
  const last = paragraphs.at(-1);
  if (!last) return null;
  const sentences = last.split(/(?<=[.!?…])\s+(?=\S)/u).filter(Boolean);
  // A last sentence too short to say anything ("Done.") keeps the one before it.
  let line = sentences.at(-1) ?? last;
  if (line.length < 16 && sentences.length > 1) line = `${sentences.at(-2)} ${line}`;
  return line;
}

/** Says a duration in milliseconds as the timeline does: "45s", "2m 13s", "1h 4m". */
export function activityDuration(milliseconds: number): string | null {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return null;
  const seconds = Math.round(milliseconds / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
