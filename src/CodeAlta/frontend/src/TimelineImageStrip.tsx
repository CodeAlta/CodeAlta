import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { Button, Spinner } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { isDialogBackdrop } from "./dialogBackdrop";
import { useShellLanguage } from "./shellLanguage";
import { steppedImageIndex, viewerWindowSize, type TimelineImage, type TimelineImageSource, type TimelineImageState } from "./timelineImages";
import type { WindowSize } from "./windowGeometry";
import { modalDialogOpen } from "./modalDialogs";

const viewerMinimum: WindowSize = { width: 360, height: 240 };
const unavailable: TimelineImageState = { status: "failed" };

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
  const [state, retry] = useTimelineImage(source, image.index, enabled);
  // An image that could not be shown is read again by a click: a busy host or a lost connection can pass.
  return <Button className="timeline-image-thumbnail" variant="minimal" title={image.title} aria-label={image.title} aria-haspopup="dialog"
    data-state={state?.status ?? "loading"} onClick={event => { if (state?.status === "failed") retry(); else onOpen(event.currentTarget); }}>
    {state?.status === "ready" ? <img src={state.url} alt="" draggable={false} />
      : state ? <AppIcon name="imageOff" size={18} /> : <span className="timeline-image-placeholder" aria-hidden="true" />}
  </Button>;
}

// Shows what the page already holds or reads by index: opening it grants no other read.
function TimelineImageViewer({ images, source, index, initialSize, onIndex, onClose }: { images: ReadonlyArray<TimelineImage>; source?: TimelineImageSource;
  index: number; initialSize: WindowSize | null; onIndex: (index: number) => void; onClose: () => void }) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const image = images[index];
  const [state] = useTimelineImage(source, image.index, true);
  const url =state?.status === "ready" ? state.url : null;
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
      if (event.key === "Escape") { event.preventDefault(); if (!event.repeat) onClose(); }
      else if (images.length > 1 && (event.key === "ArrowLeft" || event.key === "ArrowRight")) { event.preventDefault(); move(event.key === "ArrowLeft" ? -1 : 1); }
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.timeline-image.v1" title={image.title} minimumSize={viewerMinimum}
      preferredSize={viewport => viewerWindowSize(viewport, shownSize.current, viewerMinimum)}
      onClose={onClose} closeLabel={t("Close")} closeRef={close}
      headerActions={images.length > 1 && <>
        <Button variant="minimal" size="small" icon={<AppIcon name="chevronLeft" size={16} />} aria-label={t("Previous image")} title={t("Previous image")} onClick={() => move(-1)} />
        <span className="timeline-image-position">{index + 1} / {images.length}</span>
        <Button variant="minimal" size="small" icon={<AppIcon name="chevronRight" size={16} />} aria-label={t("Next image")} title={t("Next image")} onClick={() => move(1)} />
      </>}>
      <div className="timeline-image-view" data-state={state?.status ?? "loading"}>
        {url ? <img key={index} src={url} alt={image.title} draggable={false}
            onLoad={event => { const size = { width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight }; setSizes(known => new Map(known).set(index, size)); }} />
          : state ? <AppIcon name="imageOff" size={32} /> : <Spinner size={24} />}
      </div>
    </AppWindowSurface>
  </dialog>;
}
