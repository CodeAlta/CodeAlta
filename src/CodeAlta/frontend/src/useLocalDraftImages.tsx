import { useState, useSyncExternalStore, type ClipboardEvent } from "react";
import { createImageDrafts, imageHelp, imageLimits, readPastedPng } from "./promptImages";
import { translate, type Locale, type MessageKey } from "./localization";

// App supplies positive owned-local-draft eligibility and a capture of its exact lifetime.
// This is draft storage only, never image-model or Send authority.
export function useLocalDraftImages(owner: ReturnType<typeof createImageDrafts>, key: string,
  capture: () => (() => boolean) | null, invalidate: () => void, locale: Locale) {
  useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const t = (key: MessageKey, parameters?: Record<string, string | number>) => translate(locale, key, parameters);
  const images = owner.get(key);
  const [notice, setNotice] = useState(false);
  async function paste(event: ClipboardEvent<HTMLElement>) {
    if (!event.clipboardData.files.length) return;
    event.preventDefault();
    invalidate();
    const current = capture();
    const original = owner.get(key);
    const origin = event.currentTarget;
    const files = Array.from(event.clipboardData.files);
    const valid = () => current?.() && origin.isConnected && !origin.closest("[inert]") && owner.get(key) === original;
    if (!valid()) { setNotice(true); return; }
    const finish = owner.beginRead(key);
    if (!finish) { setNotice(true); return; }
    try {
      if (original.length + files.length > imageLimits.count || files.some(file => file.type !== "image/png" || file.size > imageLimits.bytes)
        || original.reduce((sum, image) => sum + atob(image.base64).length, 0) + files.reduce((sum, file) => sum + file.size, 0) > imageLimits.total)
        throw Error("Image limits");
      const added = [];
      for (const file of files) { added.push(await readPastedPng(file, `Image ${original.length + added.length + 1}`)); if (!valid()) return; }
      if (!owner.replace(key, original, [...original, ...added])) { setNotice(true); return; }
      invalidate(); setNotice(false);
    } catch { if (valid()) setNotice(true); }
    finally { finish(); }
  }
  const editable = !!capture();
  const attachments = <div className="prompt-image-attachments" aria-label={t("Prompt image attachments")}>
    <p>{t("Local PNG draft only; images stay in this window. Create and transfer never sends them.")}</p>
    <details><summary>{t("PNG attachments")}</summary><p>{t(imageHelp)}</p></details>
    {images.map((image, index) => <figure key={index}>
      <img src={`data:image/png;base64,${image.base64}`} alt={image.title} width={80} height={80} />
      <figcaption>{image.title}</figcaption>
      <label>{t("Image title")}<input aria-label={t("Image title")} maxLength={80} value={image.title} disabled={!editable}
        onCompositionStart={invalidate} onChange={event => {
          invalidate(); if (!capture()) return;
          setNotice(!owner.replace(key, images, images.map((value, i) => i === index ? { ...value, title: event.target.value } : value)));
        }} /></label>
      <button type="button" disabled={!editable} onClick={() => {
        invalidate(); if (capture()) owner.replace(key, images, images.filter((_, i) => i !== index));
      }}>{t("Remove {title}", { title: image.title })}</button>
    </figure>)}
    {notice && <p role="status">{t("Local image edit refused. Keep PNG limits and a writable owned draft; original attachments are retained.")}</p>}
  </div>;
  return { images, paste, attachments, invalidate };
}
