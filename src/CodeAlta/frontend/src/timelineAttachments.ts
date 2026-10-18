/** One file attached to a user message: its name and its media type; the page never gets its path or content. */
export type TimelineAttachment = Readonly<{ title: string; mediaType: string }>;

const maximumTitleLength = 256;
const maximumAttachments = 64;

/** Accepts the attached files of a history row; anything malformed shows none. */
export function projectTimelineAttachments(attachments: unknown): TimelineAttachment[] | undefined {
  if (!Array.isArray(attachments) || attachments.length === 0 || attachments.length > maximumAttachments) return undefined;
  const result: TimelineAttachment[] = [];
  for (const value of attachments) {
    if (!value || typeof value !== "object") return undefined;
    const { title, mediaType } = value as Record<string, unknown>;
    if (typeof title !== "string" || !title || title.length > maximumTitleLength || typeof mediaType !== "string" || !mediaType.includes("/")) return undefined;
    result.push({ title, mediaType });
  }
  return result;
}

/** A size in bytes as people read it: 512 B, 3.4 KB, 12 MB. */
export function formatAttachmentSize(bytes: number): string {
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const kilobytes = bytes / 1024;
  if (kilobytes < 1024) return `${kilobytes < 10 ? kilobytes.toFixed(1) : Math.round(kilobytes)} KB`;
  const megabytes = kilobytes / 1024;
  return `${megabytes < 10 ? megabytes.toFixed(1) : Math.round(megabytes)} MB`;
}
