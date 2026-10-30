import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { Button, Spinner } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { isDialogBackdrop } from "./dialogBackdrop";
import { isSessionContextKey } from "./sessionRowActions";
import { SessionTabMenu } from "./SessionTabMenu";
import { SettingsFileLocation } from "./SettingsFileLocation";
import { useShellLanguage } from "./shellLanguage";
import { copyTimelineImage, steppedImageIndex, viewerWindowSize, type TimelineImage, type TimelineImageSource, type TimelineImageState } from "./timelineImages";
import type { WindowSize } from "./windowGeometry";
import { modalDialogOpen } from "./modalDialogs";

const viewerMinimum: WindowSize = { width: 360, height: 240 };
const unavailable: TimelineImageState = { status: "failed" };

/** Whether an image was just copied to the clipboard, or could not be. */
export type CopyState = "idle" | "copied" | "failed";
/** Where the context menu of an image opens: under its anchor, or at the point of a right-click. */
export type ImageMenu = { anchor: HTMLElement; at?: { x: number; y: number } };

/** Copies an image to the clipboard and says for a moment whether it was copied. */
export function useImageCopy(): [CopyState, (url: string) => void] {
  const [state, setState] = useState<CopyState>("idle");
  const reset = useRef<number | undefined>(undefined);
  const sequence = useRef(0);
  useEffect(() => () => { sequence.current++; window.clearTimeout(reset.current); }, []);
  function copy(url: string) {
    const current = ++sequence.current;
    window.clearTimeout(reset.current);
    setState("idle");
    void copyTimelineImage(url).then(copied => {
      if (current !== sequence.current) return;
      setState(copied ? "copied" : "failed");
      reset.current = window.setTimeout(() => { if (current === sequence.current) setState("idle"); }, 1600);
    });
  }
  return [state, copy];
}

/** The handlers that open the context menu of an image: a right-click, the context menu key or Shift+F10. */
export function imageMenuHandlers(open: (menu: ImageMenu) => void) {
  return {
    onContextMenu: (event: MouseEvent<HTMLElement>) => { event.preventDefault(); open({ anchor: event.currentTarget, at: { x: event.clientX, y: event.clientY } }); },
    onKeyDown: (event: KeyboardEvent<HTMLElement>) => {
      if (!isSessionContextKey(event.key, event.shiftKey, event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229, false)) return;
      event.preventDefault(); event.stopPropagation();
      open({ anchor: event.currentTarget });
    },
  };
}

/**
 * One image of a message: its state (undefined while it is being read) and a way to read it again. Nothing is
 * read until `enabled`. The component keeps what it shows, so an image that leaves the cache stays on screen.
 */
export function useTimelineImage(source: TimelineImageSource | undefined, index: number, enabled: boolean): [TimelineImageState | undefined, () => void] {
  const latest = useRef(source); latest.current = source;
  const key = source?.key;
  const [attempt, setAttempt] = useState(0);
  const [shown, setShown] = useState<{ key: string | undefined; index: number; attempt: number; state: TimelineImageState } | null>(null);
  useEffect(() => {
    const current = latest.current;
    if (!current) return;
    const known = current.peek(index);
    if (known) { setShown({ key, index, attempt, state: known }); return; }
    if (!enabled) return;
    let active = true;
    void current.load(index).then(state => { if (active) setShown({ key, index, attempt, state }); },
      () => { if (active) setShown({ key, index, attempt, state: unavailable }); });
    return () => { active = false; };
  }, [key, index, enabled, attempt]);
  const retry = () => setAttempt(value => value + 1);
  if (!source) return [unavailable, retry];
  return [shown && shown.key === key && shown.index === index && shown.attempt === attempt ? shown.state : source.peek(index), retry];
}

/** The thumbnails of a user message's images; one opens the viewer on that image. */
export function TimelineImages({ images, source }: { images: ReadonlyArray<TimelineImage>; source?: TimelineImageSource }) {
  const container = useRef<HTMLDivElement>(null);
  const [seen, setSeen] = useState(() => typeof IntersectionObserver === "undefined");
  const [viewing, setViewing] = useState<{ index: number; origin: HTMLElement; size: WindowSize | null } | null>(null);
  // Images are read when their card scrolls into view, not for a whole loaded window.
  useEffect(() => {
    if (seen || !container.current) return;
    const observer = new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting)) setSeen(true); });
    observer.observe(container.current);
    return () => observer.disconnect();
  }, [seen]);
  function close() {
    const origin = viewing?.origin;
    setViewing(null);
    if (origin) requestAnimationFrame(() => { if (origin.isConnected && !origin.closest("[inert], [hidden]")) origin.focus(); });
  }
  return <div className="timeline-images" ref={container}>
    {images.map((image, position) => <TimelineThumbnail key={image.index} image={image} source={source} enabled={seen}
      onOpen={origin => {
        if (modalDialogOpen()) return;
        // The thumbnail already knows the image's size: the window opens at its final size.
        const shown = origin.querySelector("img");
        setViewing({ index: position, origin, size: shown?.naturalWidth ? { width: shown.naturalWidth, height: shown.naturalHeight } : null });
      }} />)}
    {viewing && viewing.index < images.length && <TimelineImageViewer images={images} source={source} index={viewing.index} initialSize={viewing.size}
      onIndex={index => setViewing({ ...viewing, index })} onClose={close} />}
  </div>;
}

function TimelineThumbnail({ image, source, enabled, onOpen }: { image: TimelineImage; source?: TimelineImageSource; enabled: boolean;
  onOpen: (origin: HTMLElement) => void }) {
  const { t } = useShellLanguage();
  const [state, retry] = useTimelineImage(source, image.index, enabled);
  const [copied, copy] = useImageCopy();
  const [menu, setMenu] = useState<ImageMenu | null>(null);
  const url = state?.status === "ready" ? state.url : null;
  const copyLabel = copied === "copied" ? t("Image copied") : copied === "failed" ? t("Copy failed") : "";
  // An image that could not be shown is read again by a click: a busy host or a lost connection can pass.
  return <>
    <Button className="timeline-image-thumbnail" variant="minimal" title={image.title} aria-label={image.title} aria-haspopup="dialog"
      data-state={state?.status ?? "loading"} onClick={event => { if (state?.status === "failed") retry(); else onOpen(event.currentTarget); }}
      {...(url ? imageMenuHandlers(setMenu) : null)}>
      {url ? <img src={url} alt="" draggable={false} />
        : state ? <AppIcon name="imageOff" size={18} /> : <span className="timeline-image-placeholder" aria-hidden="true" />}
      {copied !== "idle" && <span className="timeline-image-copy-state" data-state={copied} aria-hidden="true">
        <AppIcon name={copied === "copied" ? "checked" : "error"} size={14} /></span>}
    </Button>
    <span className="sr-only" aria-live="polite">{copyLabel}</span>
    {menu && url && <SessionTabMenu anchor={menu.anchor} at={menu.at} container={document.body} title={t("Image options")} current={() => true}
      onClose={() => queueMicrotask(() => setMenu(value => value === menu ? null : value))}
      items={[
        { key: "open", label: t("Open image"), icon: "zoomIn", onSelect: () => onOpen(menu.anchor) },
        { key: "copy", label: t("Copy image"), icon: "copy", onSelect: () => copy(url) },
      ]} />}
  </>;
}

/** The file of a picture of the disk, shown under it: its path, which can be copied, and a way to show it in the file manager. */
export type TimelineImageFile = Readonly<{ path: string; platform: string | null; onReveal?: () => void }>;

// The bar of the path of a file, under the picture.
const fileBarHeight = 38;

/**
 * The window of an image: the image scaled down to fit, Copy image, and the images of the same list one after the
 * other. It shows what the page already holds or reads by index: opening it grants no other read. A picture of the
 * disk also shows its `file`.
 */
export function TimelineImageViewer({ images, source, index, initialSize, onIndex, onClose, file }: { images: ReadonlyArray<TimelineImage>; source?: TimelineImageSource;
  index: number; initialSize: WindowSize | null; onIndex: (index: number) => void; onClose: () => void; file?: TimelineImageFile }) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const image = images[index];
  const [state] = useTimelineImage(source, image.index, true);
  const url =state?.status === "ready" ? state.url : null;
  const [copied, copy] = useImageCopy();
  const [menu, setMenu] = useState<ImageMenu | null>(null);
  const copyLabel = copied === "copied" ? t("Image copied") : copied === "failed" ? t("Copy failed") : t("Copy image");
  // Until an image is measured the window keeps the size it has, so moving between images does not flash.
  const [sizes, setSizes] = useState<ReadonlyMap<number, WindowSize>>(() => new Map(initialSize ? [[index, initialSize]] : []));
  const shownSize = useRef<WindowSize | null>(initialSize);
  shownSize.current = sizes.get(index) ?? shownSize.current;
  useLayoutEffect(() => {
    const element = dialog.current!;
    element.showModal(); close.current?.focus();
    return () => { if (element.open) element.close(); };
  }, []);
  const move = (step: number) => onIndex(steppedImageIndex(index, images.length, step));
  return <dialog ref={dialog} className="app-dialog timeline-image-dialog" aria-label={image.title}
    onClick={event => { event.stopPropagation(); if (isDialogBackdrop(event)) onClose(); }}
    // Effect replay can reopen the element before cleanup's queued close event arrives.
    onClose={event => { if (!event.currentTarget.open) onClose(); }} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      // Escape in the context menu closes the menu, not the window.
      if (menu) { if (event.key === "Escape") event.preventDefault(); return; }
      if (event.key === "Escape") { event.preventDefault(); if (!event.repeat) onClose(); }
      else if (images.length > 1 && (event.key === "ArrowLeft" || event.key === "ArrowRight")) { event.preventDefault(); move(event.key === "ArrowLeft" ? -1 : 1); }
      // Ctrl+C copies the image, unless text of the window is selected.
      else if ((event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === "c" && url && !event.repeat
        && !window.getSelection()?.toString()) { event.preventDefault(); copy(url); }
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.timeline-image.v1" title={image.title} minimumSize={viewerMinimum}
      preferredSize={viewport => viewerWindowSize(viewport, file && shownSize.current
        ? { width: shownSize.current.width, height: shownSize.current.height + fileBarHeight } : shownSize.current, viewerMinimum)}
      onClose={onClose} closeLabel={t("Close")} closeRef={close}
      headerActions={<>
        <Button variant="minimal" size="small" className={`timeline-image-copy copy-${copied}`} disabled={!url} aria-label={copyLabel} title={copyLabel}
          icon={<AppIcon name={copied === "copied" ? "checked" : copied === "failed" ? "error" : "copy"} size={16} />} onClick={() => { if (url) copy(url); }} />
        <span className="sr-only" aria-live="polite">{copied === "idle" ? "" : copyLabel}</span>
        {images.length > 1 && <>
          <Button variant="minimal" size="small" icon={<AppIcon name="chevronLeft" size={16} />} aria-label={t("Previous image")} title={t("Previous image")} onClick={() => move(-1)} />
          <span className="timeline-image-position">{index + 1} / {images.length}</span>
          <Button variant="minimal" size="small" icon={<AppIcon name="chevronRight" size={16} />} aria-label={t("Next image")} title={t("Next image")} onClick={() => move(1)} />
        </>}
      </>}>
      <div className="timeline-image-view" data-state={state?.status ?? "loading"}>
        {url ? <img key={index} src={url} alt={image.title} draggable={false}
            onContextMenu={event => { event.preventDefault(); setMenu({ anchor: event.currentTarget, at: { x: event.clientX, y: event.clientY } }); }}
            onLoad={event => { const size = { width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight }; setSizes(known => new Map(known).set(index, size)); }} />
          : state ? <AppIcon name="imageOff" size={32} /> : <Spinner size={24} />}
      </div>
      {file && <div className="timeline-image-file"><SettingsFileLocation path={file.path} platform={file.platform} onReveal={file.onReveal} /></div>}
    </AppWindowSurface>
    {/* The menu is placed in the dialog: outside it, it would be under the modal layer. */}
    {menu && url && dialog.current && <SessionTabMenu anchor={menu.anchor} at={menu.at} container={dialog.current} title={t("Image options")} current={() => true}
      onClose={() => queueMicrotask(() => setMenu(value => value === menu ? null : value))}
      items={[{ key: "copy", label: t("Copy image"), icon: "copy", onSelect: () => copy(url) }]} />}
  </dialog>;
}
