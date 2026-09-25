import { useLayoutEffect, useRef } from "react";
import type { BootStatus } from "#neoastra";
import { AppIcon } from "./AppIcon";
import type { PaletteAction } from "./paletteActions";

export function openAboutPaletteAction(action: PaletteAction, origin: HTMLElement | null,
  open: (origin: HTMLElement | null) => void): action is "about" {
  if (action !== "about") return false;
  open(origin);
  return true;
}

function recordedText(value: unknown, maxLength: number): string | null {
  return typeof value === "string" && value.length <= maxLength && value.trim().length > 0 &&
    !/[\u0000-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/u.test(value) ? value : null;
}

export function AboutSettingsEntry({ onOpen }: { onOpen: (origin: HTMLButtonElement) => void }) {
  return <section className="settings-card"><div className="settings-icon">i</div><div><h2>About</h2>
    <p>Inspect the running desktop host identity and its available build information.</p>
    <button type="button" className="quiet-button" onClick={event => onOpen(event.currentTarget)}>Open About</button>
  </div></section>;
}

export function AboutDialog({ status, bootError, demo, onClose }: {
  status: BootStatus | undefined; bootError: boolean; demo: boolean; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const closing = useRef(false);
  const composingEscape = useRef(false);
  useLayoutEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => { if (element?.open) element.close(); };
  }, []);
  function close() { if (!closing.current) { closing.current = true; onClose(); } }

  const browserDemo = demo || status?.state === "demo";
  const verified = !browserDemo && !bootError && !!status;
  const product = verified ? recordedText(status.productName, 128) : null;
  const version = verified ? recordedText(status.version, 256) : null;
  const versionKnown = version && version !== "development" ? version : null;
  const buildSuffix = versionKnown?.match(/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\+([0-9A-Za-z.-]+)$/u)?.[1];
  const build = recordedText(buildSuffix, 128);
  const mode = browserDemo ? "Browser demo; no running desktop host identity is available."
    : bootError ? "Desktop boot unavailable; host identity could not be verified."
      : !status ? "Waiting for the desktop boot response; host identity is not yet available."
        : status.hostAvailable && recordedText(status.hostEpoch, 256) ? "Owned desktop host (runtime availability is not assessed here)."
          : status.hostAvailable ? "Host mode unverified (no owned host identity was reported)."
            : "Catalog-only desktop host; no owned runtime is available.";
  return <dialog ref={dialog} className="app-dialog session-info-dialog about-dialog" aria-modal="true"
    aria-labelledby="about-title" aria-describedby="about-description"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (!event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) close();
      else composingEscape.current = true;
    }} onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
    <header><div><span className="eyebrow">Desktop</span><h2 id="about-title">About CodeAlta</h2></div>
      <button type="button" autoFocus className="icon-button" aria-label="Close About" onClick={close}><AppIcon name="close" size={16} /></button></header>
    <p id="about-description" className="muted-text">{mode}</p>
    <dl className="session-info-fields" tabIndex={0} aria-label="Running host build information">
      <div><dt>Product</dt><dd>{product ?? "Not available from the running host"}</dd></div>
      <div><dt>Version</dt><dd>{versionKnown ?? "Not available from the running host"}</dd></div>
      {build && <div><dt>Build metadata</dt><dd>{build}</dd></div>}
    </dl>
    <p className="muted-text">Update checks, downloads and installation are not supported in this desktop view. No update status is known.</p>
    <footer><button type="button" className="quiet-button" onClick={close}>Close</button></footer>
  </dialog>;
}
