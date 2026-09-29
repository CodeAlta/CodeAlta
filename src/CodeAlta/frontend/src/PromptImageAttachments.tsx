import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button, FormGroup, InputGroup } from "@blueprintjs/core";
import type { SessionPromptImage } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

// Presentation only: the owner revalidates the captured draft before every edit.
export function PromptImageAttachments({ images, disabled, rename, remove, notice }: {
  images: readonly SessionPromptImage[]; disabled: boolean;
  rename: (index: number, title: string) => void; remove: (index: number) => void; notice?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const [review, setReview] = useState<{ images: readonly SessionPromptImage[]; index: number } | null>(null);
  if (!images.length && !notice) return null;
  return <div className="prompt-image-attachments" aria-label={t("Prompt image attachments")}>
    {images.map((image, index) => <div className="prompt-image-tile" key={index}>
      <Button className="prompt-image-thumbnail" variant="minimal" title={image.title} aria-label={image.title}
        aria-haspopup="dialog" onClick={() => setReview({ images, index })}>
        <img src={`data:image/png;base64,${image.base64}`} alt="" width={48} height={40} />
      </Button>
      <Button className="prompt-image-remove" size="small" icon={<AppIcon name="close" size={12} />}
        aria-label={t("Remove {title}", { title: image.title })} title={t("Remove {title}", { title: image.title })}
        disabled={disabled} onClick={() => remove(index)} />
    </div>)}
    {notice && <p role="status">{notice}</p>}
    {review?.images === images && <ImageDialog image={images[review.index]} disabled={disabled}
      onClose={() => setReview(null)} rename={title => { rename(review.index, title); setReview(null); }}
      remove={() => { remove(review.index); setReview(null); }} />}
  </div>;
}

function ImageDialog({ image, disabled, onClose, rename, remove }: {
  image: SessionPromptImage; disabled: boolean; onClose: () => void;
  rename: (title: string) => void; remove: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const composing = useRef(false);
  const [title, setTitle] = useState(image.title);
  const valid = title.trim().length > 0 && title.length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/u.test(title);
  useLayoutEffect(() => {
    const element = dialog.current!;
    element.showModal(); input.current?.focus();
    return () => { if (element.open) element.close(); };
  }, []);
  return <dialog ref={dialog} className="app-dialog prompt-image-dialog" aria-label={image.title}
    onCancel={event => { event.preventDefault(); event.stopPropagation(); if (!composing.current) onClose(); }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape") {
        event.preventDefault();
        if (!event.repeat && !composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) onClose();
      }
    }}>
    <header><h2>{image.title}</h2><Button variant="minimal" aria-label={t("Close")} icon={<AppIcon name="close" size={18} />} onClick={onClose} /></header>
    <div className="prompt-image-preview"><img src={`data:image/png;base64,${image.base64}`} alt={image.title} /></div>
    <FormGroup label={t("Image title")} labelFor="prompt-image-title">
      <InputGroup id="prompt-image-title" inputRef={input} value={title} maxLength={80} disabled={disabled}
        onChange={event => setTitle(event.target.value)} />
    </FormGroup>
    <footer><Button intent="danger" disabled={disabled} onClick={remove}>{t("Remove {title}", { title: image.title })}</Button>
      <Button intent="primary" disabled={disabled || !valid} onClick={() => rename(title)}>{t("Save image title")}</Button></footer>
  </dialog>;
}
