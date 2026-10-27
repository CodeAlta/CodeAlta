// The images of the timeline of a session on the phone: where the page reads each one (always by an id the server finds
// in the records of the session, never by a path), which files of a message are images, and which picture of the disk
// that an answer shows is which image of its event.
import type { EventImage } from "./contract";

/** An image the timeline shows: what names it, its alternative text, and where the page reads it. */
export type ImageItem = Readonly<{ key: string; title: string; url: string }>;

// The types the server serves; `image/*` is an image whose type the record does not say.
const imageTypes = /^image\/(png|jpe?g|gif|webp|bmp|\*)$/iu;

/** Whether a file of a message is an image the page can show. */
export const isShownImage = (mediaType: string | null | undefined): boolean => imageTypes.test(mediaType ?? "");

/** The address of a saved copy of a session: a file of a prompt, an image a tool gave the model. */
export const savedImageUrl = (sessionId: string, id: string): string => `/api/sessions/${encodeURIComponent(sessionId)}/images/${encodeURIComponent(id)}`;

/** The address of a picture of the disk that an answer shows, by the sequence number of the answer and the position of the picture. */
export const linkedImageUrl = (sessionId: string, seq: number, position: string | number): string =>
  `/api/sessions/${encodeURIComponent(sessionId)}/linked-images/${seq}/${encodeURIComponent(String(position))}`;

/** The files of a message as the page shows them: its images as pictures (once the journal gave their ids), the others as chips. */
export function splitFiles<T extends Readonly<{ title: string; mediaType: string; id?: string | null }>>(files: readonly T[]): { images: (T & { id: string })[]; others: T[] } {
  const images: (T & { id: string })[] = [], others: T[] = [];
  for (const file of files) {
    if (isShownImage(file.mediaType) && typeof file.id === "string" && file.id) images.push(file as T & { id: string });
    else others.push(file);
  }
  return { images, others };
}

// A target as the page compares them: decoded, with the separators of either system alike.
function comparable(value: string): string {
  let text = value.trim();
  try { text = decodeURIComponent(text); } catch { /* A target that is not encoded stays as it is. */ }
  return text.replace(/\\/gu, "/");
}

/**
 * The image of the event of each picture of the disk an answer drew (`hrefs`, the targets of its links, in their order):
 * by its target, else by its place when the answer has as many as its event lists; null for a picture the event does not
 * list, which stays a link.
 */
export function matchLinkedImages(hrefs: readonly (string | null)[], images: readonly EventImage[]): (EventImage | null)[] {
  const used = new Set<EventImage>();
  const matched = hrefs.map(href => {
    if (href === null) return null;
    const target = comparable(href);
    const found = images.find(image => !used.has(image) && image.src != null && comparable(image.src) === target) ?? null;
    if (found) used.add(found);
    return found;
  });
  if (hrefs.length !== images.length) return matched;
  // As many as listed: each one not found by its target takes the next one left, in order.
  const left = images.filter(image => !used.has(image));
  return matched.map(image => image ?? left.shift() ?? null);
}

/** The image a swipe or an arrow moves to: past the last comes the first, and the reverse. */
export const steppedIndex = (current: number, count: number, step: number): number => count <= 0 ? 0 : ((current + step) % count + count) % count;
