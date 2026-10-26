import type { SessionPromptImage } from "#neoastra";
import { AttachmentError, attachmentBytes, attachmentLimits, isImageAttachment, isImageFile, readAttachedFile } from "../promptImages";
import type { PromptAttachment } from "./contract";

/** A file the phone holds until the prompt is sent: what the desktop composer sends. */
export type PhoneAttachment = PromptAttachment;

/** Most attachments of one prompt, as the host takes them. */
export const maximumAttachments = 64;

const fileLimit = attachmentLimits.fileBytes / 1024 / 1024;
const totalLimit = attachmentLimits.totalBytes / 1024 / 1024;

/**
 * Adds files the user chose to the ones already held. An image is a PNG; any other file is kept as it is. Images are
 * left out when the model does not read them. Nothing already held is dropped.
 */
export async function addAttachments(current: readonly PhoneAttachment[], files: readonly File[], imagesAllowed: boolean): Promise<{ files: readonly PhoneAttachment[]; notice: string | null }> {
  const accepted = imagesAllowed ? files : files.filter(file => !isImageFile(file));
  const refusedImages = accepted.length !== files.length;
  if (!accepted.length) return { files: current, notice: refusedImages ? "This model does not read images." : null };
  const added: PhoneAttachment[] = [];
  let total = attachmentBytes(current);
  try {
    for (const file of accepted) {
      if (current.length + added.length >= maximumAttachments) return { files: [...current, ...added], notice: `A prompt has at most ${maximumAttachments} attachments.` };
      const attachment = toPhone(await readAttachedFile(file, null));
      total += attachmentBytes([attachment]);
      if (total > attachmentLimits.totalBytes) return { files: current, notice: `A prompt's files are limited to ${totalLimit} MB in all.` };
      added.push(attachment);
    }
  } catch (error) {
    const name = error instanceof Error ? error.message : "the file";
    const unreadable = error instanceof AttachmentError && error.unreadable;
    return { files: [...current, ...added], notice: unreadable ? `Could not read ${name}.` : `Could not attach ${name}: a file of 1 byte to ${fileLimit} MB is required.` };
  }
  return { files: [...current, ...added], notice: refusedImages ? "This model does not read images. The other files were attached." : null };
}

/** Whether the attachments include an image, which needs a model that reads images. */
export const hasImages = (files: readonly PhoneAttachment[]): boolean => files.some(isImageAttachment);

function toPhone(attachment: SessionPromptImage): PhoneAttachment {
  return { title: attachment.title, mediaType: attachment.mediaType, base64: attachment.base64 };
}
