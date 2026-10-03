import { useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import type { BootStatus } from "#neoastra";
import { AboutDialog, openAboutPaletteAction } from "./AboutDialog";
import { InventoryLanguageFixture } from "./inventoryLanguage.mount";
import { CommandPalette } from "./CommandPalette";
import { createPaletteFocusRestoration, paletteAvailable, paletteShortcut, type PaletteAction, type PaletteContext } from "./paletteActions";

const one: BootStatus = { productName: "Fixture Desktop", version: "2.7.9+build.123", state: "owned-text-only",
  hostAvailable: true, hostEpoch: "epoch-one", commandReviewEnabled: false, ownedAsksEnabled: false, ownedUserInputEnabled: false };
const context: PaletteContext = { workspace: false, selection: null, epoch: null, infoReady: false, promptReady: false, searchReady: false };

function Window() {
  const [status, setStatus] = useState<BootStatus | undefined>(one);
  const [error, setError] = useState(false);
  const [demo, setDemo] = useState(false);
  const [about, setAbout] = useState(false);
  const [palette, setPalette] = useState(false);
  const [view, setView] = useState("configuration");
  const [focusRestoration] = useState(createPaletteFocusRestoration);
  useLayoutEffect(() => () => focusRestoration.cancel(), [focusRestoration]);
  const viewRef = useRef(view); viewRef.current = view;
  const captured = useRef<PaletteContext | null>(null);
  const origin = useRef<HTMLElement | null>(null);
  const aboutOrigin = useRef<{ element: HTMLElement | null; view: string } | null>(null);
  const pending = useRef<PaletteAction | null>(null);
  function openAbout(element: HTMLElement | null) {
    if (about || document.querySelector('dialog[open]')) return;
    focusRestoration.cancel();
    aboutOrigin.current = { element, view: viewRef.current };
    setAbout(true);
  }
  function closeAbout() {
    const capturedOrigin = aboutOrigin.current;
    setAbout(false);
    focusRestoration.schedule(capturedOrigin?.element ?? null, () => viewRef.current === capturedOrigin?.view,
      () => !!document.querySelector('dialog[open]'));
  }
  function openPalette() {
    if (palette || about || document.querySelector('dialog[open]')) return;
    focusRestoration.cancel();
    origin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    captured.current = context;
    setPalette(true);
  }
  function choose(action: PaletteAction) {
    if (!captured.current || !paletteAvailable(action, captured.current, context)) return;
    focusRestoration.cancel();
    pending.current = action;
    setPalette(false);
  }
  useLayoutEffect(() => {
    if (palette || !pending.current) return;
    const action = pending.current; pending.current = null;
    if (document.querySelector('dialog[open]') || !captured.current || !paletteAvailable(action, captured.current, context)) return;
    openAboutPaletteAction(action, origin.current, openAbout);
  });
  function dismissPalette() {
    setPalette(false);
    focusRestoration.schedule(origin.current, () => true, () => !!document.querySelector('dialog[open]'));
  }
  useLayoutEffect(() => {
    const keyDown = (event: KeyboardEvent) => { if (paletteShortcut(event, palette || about)) { event.preventDefault(); openPalette(); } };
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  });
  Object.assign(window, { aboutFixture: { setStatus, setError, setDemo, setView,
    paletteOrigin: () => origin.current,
    product: one, catalog: { ...one, hostAvailable: false, hostEpoch: null, state: "in-development" } } });
  return <div className="app-shell"><header className="topbar"><button id="commands" type="button" onClick={openPalette}>Commands</button>
    <button id="settings" type="button" onClick={() => setView("configuration")}>Settings</button></header>
    {view === "configuration" && <main className="configuration-page"><div className="settings-grid"><button type="button" className="quiet-button" onClick={event => openAbout(event.currentTarget)}>Open About</button></div></main>}
    {palette && captured.current && <CommandPalette context={context} captured={captured.current} onChoose={choose} onClose={dismissPalette} />}
    {about && <AboutDialog status={status} bootError={error} demo={demo} onClose={closeAbout} />}
  </div>;
}

createRoot(document.getElementById("app")!).render(<InventoryLanguageFixture><Window /></InventoryLanguageFixture>);
