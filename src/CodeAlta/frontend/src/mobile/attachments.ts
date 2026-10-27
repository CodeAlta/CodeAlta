import type { SessionPromptImage } from "#neoastra";
import { AttachmentError, attachmentBytes, attachmentLimits, isImageAttachment, isImageFile, readAttachedFile } from "../promptImages";
import type { Choice } from "./models";
import type { Options, PromptAttachment } from "./contract";

/** A file the phone holds until the prompt is sent: what the desktop composer sends. */
export type PhoneAttachment = PromptAttachment;

/** Most attachments of one prompt, as the host takes them. */
export const maximumAttachments = 64;

/**
 * The longest side of a photo the phone sends: a camera's 12 MP picture as a PNG is tens of megabytes, larger than a file
 * may be, and more than a model reads. The host takes images as PNG only.
 */
export const phoneImageSide = 2048;

const fileLimit = attachmentLimits.fileBytes / 1024 / 1024;
const totalLimit = attachmentLimits.totalBytes / 1024 / 1024;

/**
 * Adds files the user chose to the ones already held. An image is a PNG of at most `phoneImageSide` pixels on its longest side;
 * any other file is kept as it is. Images are left out when the model does not read them. Every file, an image too, is at most
 * 20 MB and all of them 64 MB, checked here, before anything is sent. Nothing already held is dropped.
 */
export async function addAttachments(current: readonly PhoneAttachment[], files: readonly File[], imagesAllowed: boolean,
  read: (file: File) => Promise<SessionPromptImage> = file => readAttachedFile(file, null, phoneImageSide)): Promise<{ files: readonly PhoneAttachment[]; notice: string | null }> {
  const accepted = imagesAllowed ? files : files.filter(file => !isImageFile(file));
  const refusedImages = accepted.length !== files.length;
  if (!accepted.length) return { files: current, notice: refusedImages ? "This model does not read images." : null };
  const added: PhoneAttachment[] = [];
  let total = attachmentBytes(current);
  const tooLarge = (name: string) => `Could not attach ${name}: a file of 1 byte to ${fileLimit} MB is required.`;
  const overTotal = `A prompt's files are limited to ${totalLimit} MB in all.`;
  try {
    for (const file of accepted) {
      if (current.length + added.length >= maximumAttachments) return { files: [...current, ...added], notice: `A prompt has at most ${maximumAttachments} attachments.` };
      // A file that is not an image keeps its size: it is refused before it is read.
      if (!isImageFile(file) && total + file.size > attachmentLimits.totalBytes) return { files: [...current, ...added], notice: overTotal };
      const attachment = toPhone(await read(file));
      const bytes = attachmentBytes([attachment]);
      if (bytes > attachmentLimits.fileBytes) return { files: [...current, ...added], notice: tooLarge(file.name) };
      if (total + bytes > attachmentLimits.totalBytes) return { files: [...current, ...added], notice: overTotal };
      total += bytes;
      added.push(attachment);
    }
  } catch (error) {
    const name = error instanceof Error ? error.message : "the file";
    const unreadable = error instanceof AttachmentError && error.unreadable;
    return { files: [...current, ...added], notice: unreadable ? `Could not read ${name}.` : tooLarge(name) };
  }
  return { files: [...current, ...added], notice: refusedImages ? "This model does not read images. The other files were attached." : null };
}

/** Whether the attachments include an image, which needs a model that reads images. */
export const hasImages = (files: readonly PhoneAttachment[]): boolean => files.some(isImageAttachment);

/**
 * Whether the model a new session starts on reads images: the model of the tier chosen, or the model chosen, of its provider.
 * Null while the options are not known; a model that does not say is one that does not read them.
 */
export function newSessionReadsImages(options: Options | null, choice: Choice): boolean | null {
  if (!options) return null;
  const target = choice.tier ? options.tiers.find(tier => tier.tier === choice.tier) : { provider: choice.provider, model: choice.model };
  if (!target?.model) return null;
  return options.models.find(model => model.id === target.model && (!target.provider || model.provider === target.provider))?.imageInput === true;
}

/**
 * The files the phone holds, for each session and for the new-session screen. The ones of the screens the user is on stay; the
 * others are let go, the least recent first, once all of them would hold more than `cap` bytes, and are named by `wasDropped`.
 */
export function createHeldFiles(cap = 2 * attachmentLimits.totalBytes) {
  const held = new Map<string, readonly PhoneAttachment[]>();
  const dropped = new Set<string>();
  const empty: readonly PhoneAttachment[] = Object.freeze([]);
  const total = () => [...held.values()].reduce((sum, files) => sum + attachmentBytes(files), 0);
  return {
    get: (key: string): readonly PhoneAttachment[] => held.get(key) ?? empty,
    /** Holds the files of a key, the most recent; `keep` are the keys never let go now. Returns the keys let go. */
    set(key: string, files: readonly PhoneAttachment[], keep: readonly string[] = []): string[] {
      held.delete(key);
      dropped.delete(key);
      if (files.length) held.set(key, Object.freeze([...files]));
      const gone: string[] = [];
      for (const other of [...held.keys()]) {
        if (total() <= cap) break;
        if (other === key || keep.includes(other)) continue;
        held.delete(other);
        dropped.add(other);
        gone.push(other);
      }
      return gone;
    },
    /** The files of a key, which are no longer held. */
    take(key: string): readonly PhoneAttachment[] {
      const files = held.get(key) ?? empty;
      held.delete(key);
      return files;
    },
    /** Whether the files of a key were let go since it last held some; asked once. */
    wasDropped(key: string): boolean { return dropped.delete(key); },
    total,
  };
}

function toPhone(attachment: SessionPromptImage): PhoneAttachment {
  return { title: attachment.title, mediaType: attachment.mediaType, base64: attachment.base64 };
}
