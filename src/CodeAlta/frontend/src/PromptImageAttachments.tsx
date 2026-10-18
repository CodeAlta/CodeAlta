import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button, FormGroup, InputGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { SessionPromptImage } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { attachmentBytes, attachmentLink, isImageAttachment, isLinkAttachment, linkMediaTypes } from "./promptImages";
import { formatAttachmentSize } from "./timelineAttachments";
import { useShellLanguage } from "./shellLanguage";
import { useSurfaceDensity } from "./promptSurface/PromptSurfaceParts";
import { commandDefinitions, commandKeys, type CommandId } from "./commandRegistry";

// Presentation only: the owner revalidates the captured draft before every edit. An image shows its thumbnail, a file
// its icon and its name.
export function PromptImageAttachments({ images, disabled, rename, remove, notice }: {
  images: readonly SessionPromptImage[]; disabled: boolean;
  rename: (index: number, title: string) => void; remove: (index: number) => void; notice?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const [review, setReview] = useState<{ images: readonly SessionPromptImage[]; index: number } | null>(null);
  if (!images.length && !notice) return null;
  return <div className="prompt-image-attachments" aria-label={t("Prompt image attachments")}>
    {images.map((image, index) => <div className="prompt-image-tile" key={index}>
      {/* A link (an issue, a folder) has nothing to preview or rename: its tooltip is the link the prompt gets. */}
      <Button className={isImageAttachment(image) ? "prompt-image-thumbnail" : "prompt-image-thumbnail prompt-file-thumbnail"} variant="minimal"
        icon={isImageAttachment(image) ? undefined : <AppIcon name={fileIcon(image.mediaType)} size={18} />}
        title={isLinkAttachment(image) ? attachmentLink(image) : image.title} aria-label={image.title} aria-haspopup={isLinkAttachment(image) ? undefined : "dialog"}
        onClick={() => { if (!isLinkAttachment(image)) setReview({ images, index }); }}>
        {isImageAttachment(image) ? <img src={`data:image/png;base64,${image.base64}`} alt="" width={48} height={40} />
          : <span className="prompt-file-name">{image.title}</span>}
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

/** What the attach menu of a composer does: files are read by the page, a folder and issues become links in the prompt. */
export type AttachActions = Readonly<{
  /** Takes the files the user chose in the file dialog. */
  onFiles: (files: File[], origin: HTMLElement) => void;
  /** Asks for a folder and puts a link to it in the prompt; absent where there is no folder dialog. */
  onFolder?: (() => void) | null;
  /** Opens the issue picker at the caret; absent where the project has no issues to pick. */
  onIssues?: (() => void) | null;
}>;

/**
 * The attach button of a composer, before its model: a menu that adds files or photos (the file dialog of the system,
 * several files at once), a folder (the folder dialog, opened in the folder of the session) or issues (the issue picker,
 * several at once). The page gets the content of the files, as from a drop, never their paths. On the tiniest surface
 * the button gives its room to the model and the `⋯` menu has the same entries (`AttachMenuItems`); the file input stays
 * here, out of any menu, so that closing a menu does not lose the choice.
 */
export function AttachMenu({ id, disabled, onFiles, onFolder, onIssues }: AttachActions & { id: string; disabled: boolean }) {
  const { t } = useShellLanguage();
  const input = useRef<HTMLInputElement>(null);
  const tiny = useSurfaceDensity() === "tiny";
  // The commands of the application (attach_files Ctrl+U, attach_folder Ctrl+Alt+F, attach_issues Ctrl+Alt+I) reach the
  // composer in front through its file input, with a `codealta-attach` event naming the entry.
  const latest = useRef({ onFolder, onIssues }); latest.current = { onFolder, onIssues };
  useEffect(() => {
    const element = input.current;
    if (!element) return;
    const run = (event: Event) => {
      const entry = (event as CustomEvent<string>).detail;
      if (element.disabled) return;
      if (entry === "files") element.click();
      else if (entry === "folder") latest.current.onFolder?.();
      else if (entry === "issues") latest.current.onIssues?.();
    };
    element.addEventListener(attachEvent, run);
    return () => element.removeEventListener(attachEvent, run);
  }, []);
  const menu = <Menu aria-label={t("Attach")}>
    <MenuItem icon={<AppIcon name="attach" size={16} />} text={t("Add files or photos")} label={gesture("attachFiles")} title={t("Attach files (or drop or paste them)")}
      onClick={() => input.current?.click()} />
    {onFolder && <MenuItem icon={<AppIcon name="folder" size={16} />} text={t("Add folder…")} label={gesture("attachFolder")} onClick={onFolder} />}
    {onIssues && <MenuItem icon={<AppIcon name="issueOpen" size={16} />} text={t("Add issues…")} label={gesture("attachIssues")} onClick={onIssues} />}
  </Menu>;
  return <>
    {!tiny && <PopoverNext content={menu} placement="top-start" disabled={disabled}>
      <Button variant="minimal" className="composer-attach" icon={<AppIcon name="attach" size={16} />} disabled={disabled}
        aria-label={t("Attach")} title={t("Add files, a folder or issues")} aria-haspopup="menu" />
    </PopoverNext>}
    <input ref={input} id={id} type="file" multiple hidden tabIndex={-1} aria-hidden="true" disabled={disabled} data-attach-input="" onChange={event => {
      const target = event.currentTarget;
      const files = Array.from(target.files ?? []);
      // The same file can be chosen again.
      target.value = "";
      if (files.length) onFiles(files, target);
    }} />
  </>;
}

// The gesture of a command on this platform (Cmd+U on macOS, Ctrl+U elsewhere), as the help lists it.
const gesture = (id: CommandId) => commandKeys(commandDefinitions.find(command => command.id === id)!)[0];

/** The event the commands of the application send to the file input of `AttachMenu`: its detail is `files`, `folder` or `issues`. */
export const attachEvent = "codealta-attach";

/** The entries of `AttachMenu` in the `⋯` menu, where the surface is too narrow for the button. */
export function AttachMenuItems({ inputId, disabled, onFolder, onIssues }: Omit<AttachActions, "onFiles"> & { inputId: string; disabled: boolean }) {
  const { t } = useShellLanguage();
  if (useSurfaceDensity() !== "tiny") return null;
  return <>
    <Button variant="minimal" icon={<AppIcon name="attach" size={16} />} disabled={disabled} title={t("Attach files (or drop or paste them)")}
      onClick={() => (document.getElementById(inputId) as HTMLInputElement | null)?.click()}>{t("Add files or photos")}</Button>
    {onFolder && <Button variant="minimal" icon={<AppIcon name="folder" size={16} />} disabled={disabled} onClick={onFolder}>{t("Add folder…")}</Button>}
    {onIssues && <Button variant="minimal" icon={<AppIcon name="issueOpen" size={16} />} disabled={disabled} onClick={onIssues}>{t("Add issues…")}</Button>}
  </>;
}

/** The files attached to a sent prompt, by name: the page has neither their paths nor their content. */
export function AttachedFileChips({ attachments }: { attachments: ReadonlyArray<Readonly<{ title: string; mediaType: string }>> }) {
  const { t } = useShellLanguage();
  return <ul className="timeline-attachments" aria-label={t("Attached files")}>
    {attachments.map((file, index) => <li key={index} className="timeline-attachment" title={`${file.title} · ${file.mediaType}`}>
      <AppIcon name={fileIcon(file.mediaType)} size={14} /><span>{file.title}</span></li>)}
  </ul>;
}

/** The icon of an attachment, from its media type: a link of the attach menu has the icon of what it names. */
export function fileIcon(mediaType: string | null | undefined): "fileText" | "fileImage" | "fileGeneric" | "issueOpen" | "folder" {
  return !mediaType ? "fileGeneric" : mediaType === linkMediaTypes.issue ? "issueOpen" : mediaType === linkMediaTypes.folder ? "folder"
    : mediaType.startsWith("image/") ? "fileImage"
    : mediaType.startsWith("text/") || mediaType === "application/pdf" || mediaType === "application/json" ? "fileText" : "fileGeneric";
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
  const picture = isImageAttachment(image);
  const valid = title.trim().length > 0 && title.length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/u.test(title) && (picture || !/[\\/]/.test(title));
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
    {picture ? <div className="prompt-image-preview"><img src={`data:image/png;base64,${image.base64}`} alt={image.title} /></div>
      : <p className="prompt-file-facts"><AppIcon name={fileIcon(image.mediaType)} size={18} /><span>{image.mediaType} · {formatAttachmentSize(attachmentBytes([image]))}</span></p>}
    <FormGroup label={t(picture ? "Image title" : "File name")} labelFor="prompt-image-title">
      <InputGroup id="prompt-image-title" inputRef={input} value={title} maxLength={80} disabled={disabled}
        onChange={event => setTitle(event.target.value)} />
    </FormGroup>
    <footer><Button intent="danger" disabled={disabled} onClick={remove}>{t("Remove {title}", { title: image.title })}</Button>
      <Button intent="primary" disabled={disabled || !valid} onClick={() => rename(title)}>{t(picture ? "Save image title" : "Save file name")}</Button></footer>
  </dialog>;
}
