import { useState, useSyncExternalStore, type ClipboardEvent } from "react";
import type { SessionPromptImage } from "#neoastra";
import { AttachmentError, attachmentBytes, attachmentLimits, createImageDrafts, readAttachedFile } from "./promptImages";
import { translate, type Locale, type MessageKey } from "./localization";
import { PromptImageAttachments } from "./PromptImageAttachments";

// App supplies positive owned-local-draft eligibility and a capture of its exact lifetime.
// This is draft storage only, never image-model or Send authority.
export function useLocalDraftImages(owner: ReturnType<typeof createImageDrafts>, key: string,
  capture: () => (() => boolean) | null, invalidate: () => void, locale: Locale) {
  useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const t = (key: MessageKey, parameters?: Record<string, string | number>) => translate(locale, key, parameters);
  const images = owner.get(key);
  // What the last edit was refused for: true for the general refusal, or the message of a file that could not be attached.
  const [notice, setNotice] = useState<boolean | string>(false);
  function paste(event: ClipboardEvent<HTMLElement>) {
    if (!event.clipboardData.files.length) return;
    event.preventDefault();
    void attach(Array.from(event.clipboardData.files), event.currentTarget, true);
  }
  // The files chosen, dropped or pasted: an image as a PNG, any other file as it is.
  async function attach(files: readonly File[], origin: HTMLElement, pasted = false) {
    if (!files.length) return;
    invalidate();
    const current = capture();
    const original = owner.get(key);
    const valid = () => current?.() && origin.isConnected && !origin.closest("[inert]") && owner.get(key) === original;
    if (!valid()) { setNotice(true); return; }
    const finish = owner.beginRead(key);
    if (!finish) { setNotice(true); return; }
    try {
      const added = [];
      let total = attachmentBytes(original);
      for (const file of files) {
        const attachment = await readAttachedFile(file, pasted ? `Image ${original.length + added.length + 1}` : null);
        if (!valid()) return;
        total += attachmentBytes([attachment]);
        if (total > attachmentLimits.totalBytes) { setNotice(t("The attachments of a prompt are limited to {size} MB in all.", { size: attachmentLimits.totalBytes / 1024 / 1024 })); return; }
        added.push(attachment);
      }
      if (!owner.replace(key, original, [...original, ...added])) { setNotice(true); return; }
      invalidate(); setNotice(false);
    } catch (error) {
      if (!valid()) return;
      setNotice(!(error instanceof AttachmentError) ? true : error.unreadable ? t("Could not read {name}: a folder cannot be attached.", { name: error.message })
        : t("Could not attach {name}: a file of 1 byte to {size} MB is required.", { name: error.message, size: attachmentLimits.fileBytes / 1024 / 1024 }));
    }
    finally { finish(); }
  }
  // The links of the attach menu (issues, a folder), once each: they become text when the prompt is sent.
  function addLinks(links: readonly SessionPromptImage[]) {
    invalidate();
    if (!capture()) { setNotice(true); return; }
    const current = owner.get(key);
    const added = links.filter(link => !current.some(item => item.mediaType === link.mediaType && item.base64 === link.base64));
    if (added.length) setNotice(!owner.replace(key, current, [...current, ...added]));
  }
  const editable = !!capture();
  const attachments = <PromptImageAttachments images={images} disabled={!editable}
    notice={typeof notice === "string" ? notice : notice ? t("Local image edit refused. Use a supported image and a writable owned draft; original attachments are retained.") : null}
    rename={(index, title) => {
      invalidate(); if (!capture()) return;
      setNotice(!owner.replace(key, images, images.map((value, i) => i === index ? { ...value, title } : value)));
    }} remove={index => {
      invalidate(); if (capture()) setNotice(!owner.replace(key, images, images.filter((_, i) => i !== index)));
    }} />;
  return { images, paste, attach, addLinks, attachments, invalidate };
}
