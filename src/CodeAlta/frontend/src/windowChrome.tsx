import { useEffect, useState, type ReactNode } from "react";
import { createDesktopClient, invoke, subscribe, type DesktopRpc, type DesktopWindowSnapshot, type NeoRpcCallOptions } from "@neoastra/client";
import { neoRpcContractHash } from "#neoastra";
import logo from "../../../../img/CodeAlta.svg";
import { useShellLanguage } from "./shellLanguage";

// The host checks the contract of every call, including the desktop handlers'.
const rpc: DesktopRpc = {
  invoke: <TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions) =>
    invoke<TRequest, TResult>(command, args, { ...options, contractHash: neoRpcContractHash }),
  subscribe: <T,>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions) =>
    subscribe<T>(event, handler, { ...options, contractHash: neoRpcContractHash }),
};
const desktop = createDesktopClient(rpc);

/** The application mark. */
export const logoUrl: string = logo;

/**
 * Attaches the document to the native title bar: the client then publishes the layout as
 * `--neoastra-titlebar-*` custom properties and `data-neoastra-*` attributes on the root element, and
 * makes `data-neoastra-drag-region` elements move the window. Without a desktop host (browser demo)
 * nothing is attached and the page keeps its plain layout.
 */
export function useWindowTitleBar(): DesktopWindowSnapshot | undefined {
  const [snapshot, setSnapshot] = useState<DesktopWindowSnapshot>();
  useEffect(() => {
    let disposed = false;
    let dispose: (() => Promise<void>) | undefined;
    void desktop.window.attachTitleBar({ onChange: setSnapshot }).then(binding => {
      if (disposed) void binding.dispose();
      else dispose = binding.dispose;
    }).catch(() => { /* No desktop window to attach to. */ });
    return () => { disposed = true; if (dispose) void dispose(); };
  }, []);
  return snapshot;
}

/** Asks the host to close the main window, which ends the application like its close button does. */
export function closeApplicationWindow(): void {
  void desktop.window.close().catch(() => { /* Not a desktop window: nothing to close. */ });
}

/**
 * The application mark at the start of the title bar, followed by the workspace navigation (`children`);
 * the area around them moves the window. The name is not written: the room goes to the navigation.
 */
export function WindowBrand({ developer = false, children }: { developer?: boolean; children?: ReactNode }) {
  return <div className="window-brand" data-neoastra-drag-region>
    <img className="window-brand-mark" src={logoUrl} alt="CodeAlta" title="CodeAlta" draggable={false} />
    {developer && <span className="window-brand-tag">dev</span>}
    {children}
  </div>;
}

/**
 * Minimize, maximize/restore and close buttons for windows whose platform draws none over the page
 * (the `Hidden` title-bar style, or a backend without native overlay controls).
 */
export function WindowControls({ snapshot }: { snapshot: DesktopWindowSnapshot | undefined }) {
  const { t } = useShellLanguage();
  if (!snapshot || snapshot.titleBar.style === "Default" || snapshot.state === "Fullscreen"
    || snapshot.titleBar.leftInset > 0 || snapshot.titleBar.rightInset > 0) return null;
  const maximized = snapshot.state === "Maximized";
  return <div className="window-controls" data-neoastra-no-drag>
    <button type="button" aria-label={t("Minimize")} title={t("Minimize")} onClick={() => void desktop.window.minimize()}>
      <svg viewBox="0 0 10 10" aria-hidden="true"><path d="M0 5h10" /></svg>
    </button>
    <button type="button" aria-label={t(maximized ? "Restore" : "Maximize")} title={t(maximized ? "Restore" : "Maximize")}
      onClick={() => void (maximized ? desktop.window.restore() : desktop.window.maximize())}>
      <svg viewBox="0 0 10 10" aria-hidden="true">{maximized ? <path d="M2.5 2.5v-2h7v7h-2M.5 2.5h7v7h-7z" /> : <path d="M.5.5h9v9h-9z" />}</svg>
    </button>
    <button type="button" className="window-close" aria-label={t("Close")} title={t("Close")} onClick={closeApplicationWindow}>
      <svg viewBox="0 0 10 10" aria-hidden="true"><path d="M0 0l10 10M10 0L0 10" /></svg>
    </button>
  </div>;
}
