import { useContext, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { AppWindow } from "../AppWindow";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { McpAppBridge, McpAppError, type McpAppDisplayMode, type McpAppHostContext } from "./mcpAppBridge";
import { hostStyles, McpAppsContext, type McpAppOpened, type McpAppsHost } from "./mcpAppHost";

/** The tallest a view grows under its row; it scrolls inside beyond. */
export const inlineMaximumHeight = 600;
const inlineMinimumHeight = 32;
// How long a frame that left the page keeps its token: a remount of React's strict mode takes it back at once.
const closeDelayMs = 1000;
// How long after the user pressed in a view it may send a message to the session, and how often.
const messageAfterUseMs = 15_000;
const messageIntervalMs = 2_000;

// A view in its frame: an origin of its own (mcpapp://), scripts and forms, nothing of the window but its messages.
const sandbox = "allow-scripts allow-same-origin allow-forms";

function parse(json: string | null | undefined, fallback: unknown): unknown {
  if (!json) return fallback;
  try { return JSON.parse(json) as unknown; } catch { return fallback; }
}

type FrameProps = {
  host: McpAppsHost; view: McpAppOpened; displayMode: McpAppDisplayMode;
  /** Asks for a display mode; returns the mode the view is shown in after it. */
  onDisplayMode: (mode: string) => McpAppDisplayMode;
  /** Set when the view is about to go: it is told, then `onClosed` runs. */
  closing?: string | null; onClosed?: () => void;
};

/** The frame of a view and its conversation with the window (McpAppBridge). */
export function McpAppFrame({ host, view, displayMode, onDisplayMode, closing = null, onClosed }: FrameProps) {
  const { t } = useShellLanguage();
  const frame = useRef<HTMLIFrameElement>(null);
  const bridge = useRef<McpAppBridge | null>(null);
  const closer = useRef<number | undefined>(undefined);
  const [height, setHeight] = useState(120);
  const [left, setLeft] = useState(false);
  // The host is read when it is used: a new host of the pane (a new epoch) does not start the conversation again, which
  // the view, already initialized, could not join.
  const latest = useRef({ host, displayMode, onDisplayMode, onClosed });
  latest.current = { host, displayMode, onDisplayMode, onClosed };

  // A layout effect: the frame's listeners are there before its document can load, in the task that inserts it.
  useLayoutEffect(() => {
    const element = frame.current;
    if (!element) return;
    window.clearTimeout(closer.current);
    const hostOf = () => latest.current.host;
    let loads = 0;
    // A message of a view goes to the session while the user works in the view: the frame has the keyboard, or had it
    // a moment ago. A view that speaks on its own, or too fast, is refused.
    let usedAt = Number.NEGATIVE_INFINITY, sentAt = Number.NEGATIVE_INFINITY;
    const used = () => window.setTimeout(() => { if (document.activeElement === element) usedAt = Date.now(); }, 0);
    // The origin of the view once it spoke: the window's messages go to that document only.
    let target = "*";
    const tool = parse(view.toolDefinition, null);
    const context = (): McpAppHostContext => ({
      ...hostStyles(),
      displayMode: latest.current.displayMode,
      availableDisplayModes: ["inline", "fullscreen"],
      containerDimensions: latest.current.displayMode === "inline"
        ? { width: element.clientWidth, maxHeight: inlineMaximumHeight }
        : { width: element.clientWidth, height: element.clientHeight },
      locale: navigator.language,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      platform: "desktop",
      userAgent: `CodeAlta/${hostOf().hostVersion}`,
      ...(tool ? { toolInfo: { tool } } : {}),
    });
    // The view speaks while its document loads, before the frame's load event: the window hears it until a second document.
    const created = new McpAppBridge(message => { if (loads <= 1) element.contentWindow?.postMessage(message, target); }, {
      callTool: (name, args) => hostOf().callTool(view.token, name, args),
      readResource: uri => hostOf().readResource(view.token, uri),
      sendMessage: async text => {
        const now = Date.now();
        if (document.activeElement !== element && now - usedAt > messageAfterUseMs) throw new McpAppError(-32000, "A message of a view is sent while the user works in it.");
        if (now - sentAt < messageIntervalMs) throw new McpAppError(-32000, "A message of the view was sent a moment ago.");
        sentAt = now;
        return await hostOf().sendMessage(text);
      },
      openLink: url => hostOf().openLink(url),
      requestDisplayMode: mode => latest.current.onDisplayMode(mode),
      sizeChanged: size => {
        if (size.height !== undefined) setHeight(Math.max(inlineMinimumHeight, Math.min(inlineMaximumHeight, Math.ceil(size.height))));
      },
      log: () => undefined,
    }, { toolInput: parse(view.toolInput, {}) as Record<string, unknown>, toolResult: parse(view.toolResult, { content: [] }), context, hostVersion: hostOf().hostVersion });
    bridge.current = created;
    const received = (event: MessageEvent) => {
      if (event.source !== element.contentWindow || loads > 1) return;
      // The origin of the view's first message is the view's: a document of another origin in the frame is not heard.
      if (target === "*") { if (event.origin && event.origin !== "null") target = event.origin; }
      else if (event.origin !== target) return;
      created.receive(event.data);
    };
    // A second document in the frame is not the view: the window no longer talks to it. (The page's own policy,
    // `frame-src mcpapp:`, keeps the frame from going to any other scheme, and the host serves an origin only its view.)
    const loaded = () => { if (++loads > 1) { created.stop(); setLeft(true); } };
    window.addEventListener("message", received);
    // The window loses the keyboard to the frame: the user pressed in the view.
    window.addEventListener("blur", used);
    element.addEventListener("load", loaded);
    const theme = new MutationObserver(() => created.contextChanged(hostStyles()));
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-color-scheme", "data-palette", "class", "style"] });
    // A new width is told; a new height is the view's own answer to its size, not told back to it.
    let width = element.clientWidth;
    const resized = new ResizeObserver(() => {
      if (element.clientWidth === width && latest.current.displayMode === "inline") return;
      width = element.clientWidth;
      created.contextChanged({ containerDimensions: context().containerDimensions });
    });
    resized.observe(element.parentElement ?? element);
    return () => {
      window.removeEventListener("message", received);
      window.removeEventListener("blur", used);
      element.removeEventListener("load", loaded);
      theme.disconnect();
      resized.disconnect();
      // A view removed with its row (another session, a closed pane) is told, without waiting for its answer.
      void created.teardown("The view is closed.", 0);
      bridge.current = null;
      closer.current = window.setTimeout(() => latest.current.host.close(view.token), closeDelayMs);
    };
  }, [view]);

  useEffect(() => {
    if (!closing) return;
    let done = false;
    const finish = () => { if (!done) { done = true; latest.current.onClosed?.(); } };
    void (bridge.current?.teardown(closing) ?? Promise.resolve()).then(finish, finish);
  }, [closing]);

  // A mode change is told to the view; the size of the window then follows.
  useEffect(() => { bridge.current?.contextChanged({ displayMode }); }, [displayMode]);

  const label = t("View of {server}", { server: view.server });
  return <div className="mcp-app-frame" data-mode={displayMode}>
    {left ? <p className="mcp-app-notice">{t("The view left its page.")}</p>
      : <iframe ref={frame} src={view.frameUrl} title={label} sandbox={sandbox} allow="" referrerPolicy="no-referrer"
        style={displayMode === "inline" ? { height } : undefined} />}
  </div>;
}

type SlotState =
  | { kind: "idle" } | { kind: "opening" } | { kind: "failed"; error: string }
  | { kind: "inline"; view: McpAppOpened; closing: string | null; next: "idle" | "window" | null }
  | { kind: "window"; view: McpAppOpened | null; closing: boolean };

type SlotProps = {
  offset: string; server: string; timestamp: string;
  /** Opens the window of the call of the view, from the button that asked; null shows the heading as text. */
  onOpenCall?: ((origin: HTMLButtonElement) => void) | null;
};

/**
 * The view of a tool call, under the activity that holds the call: shown at once for a call that ended while its pane
 * is open, on demand for an older one. Its heading leads back to its call.
 */
export function McpAppSlot(props: SlotProps) {
  const host = useContext(McpAppsContext);
  return host ? <McpAppSlotOf host={host} {...props} /> : null;
}

function McpAppSlotOf({ host, offset, server, timestamp, onOpenCall = null }: SlotProps & { host: McpAppsHost }) {
  const { t } = useShellLanguage();
  const titleId = useId();
  const slot = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<SlotState>({ kind: "idle" });
  const shownOnce = useRef(false);
  const mounted = useRef(true);
  const stateRef = useRef(state); stateRef.current = state;
  const live = Date.parse(timestamp) >= host.startedAt;

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; host.live.release(offset); };
  }, [host, offset]);

  async function open(into: "inline" | "window") {
    if (into === "inline") setState({ kind: "opening" });
    let opened: Awaited<ReturnType<McpAppsHost["open"]>>;
    // A host that does not answer (a restart, a timeout) is a failure the button tries again.
    try { opened = await host.open(offset); } catch (error) { opened = { error: error instanceof Error ? error.message : String(error) }; }
    if (!mounted.current) { if (opened.view) host.close(opened.view.token); return; }
    if (!opened.view) { setState({ kind: "failed", error: opened.error }); return; }
    if (into === "window") {
      const view = opened.view;
      const current = stateRef.current;
      // The window closed while its view opened: the view is not shown, and its token goes.
      if (current.kind !== "window" || current.closing) { host.close(view.token); return; }
      setState({ ...current, view });
      return;
    }
    setState({ kind: "inline", view: opened.view, closing: null, next: null });
    host.live.claim(offset, () => setState(previous => previous.kind === "inline" ? { ...previous, closing: "Too many views are open.", next: "idle" } : previous));
  }

  // A call of this run of the window shows its view once its row is in sight; an older one waits for a press.
  useEffect(() => {
    const element = slot.current;
    if (!live || shownOnce.current || !element) return;
    const sight = new IntersectionObserver(entries => {
      if (!entries.some(entry => entry.isIntersecting) || shownOnce.current || stateRef.current.kind !== "idle") return;
      shownOnce.current = true;
      sight.disconnect();
      void open("inline");
    });
    sight.observe(element);
    return () => sight.disconnect();
  }, [live]);

  function displayMode(mode: string): McpAppDisplayMode {
    const current = stateRef.current;
    if (mode === "fullscreen" && current.kind === "inline" && !current.closing) {
      setState({ ...current, closing: "The view moves to a window.", next: "window" });
      return "fullscreen";
    }
    if (mode === "inline" && current.kind === "window" && !current.closing) {
      setState({ ...current, closing: true });
      return "inline";
    }
    return current.kind === "window" ? "fullscreen" : "inline";
  }

  function closedInline() {
    const current = stateRef.current;
    if (current.kind !== "inline") return;
    host.live.release(offset);
    if (current.next === "window") { setState({ kind: "window", view: null, closing: false }); void open("window"); }
    else setState({ kind: "idle" });
  }

  function closedWindow() {
    // Back under its row, where it is shown again.
    void open("inline");
  }

  // The window closes: its view is told first; a window whose view is not open yet just goes.
  function closeWindow() {
    const current = stateRef.current;
    if (current.kind !== "window" || current.closing) return;
    if (current.view) setState({ ...current, closing: true });
    else closedWindow();
  }

  const label = t("View of {server}", { server });
  return <div ref={slot} className="mcp-app-view" data-border={state.kind === "inline" && state.view.prefersBorder === false ? "false" : "true"}>
    {onOpenCall
      ? <button type="button" className="mcp-app-heading" aria-haspopup="dialog" title={t("Show the call of this view")}
        onClick={event => onOpenCall(event.currentTarget)}><AppIcon name="server" size={13} /><span>{label}</span></button>
      : <div className="mcp-app-heading"><AppIcon name="server" size={13} /><span>{label}</span></div>}
    {state.kind === "idle" && <Button size="small" variant="outlined" icon={<AppIcon name="open" size={14} />} onClick={() => void open("inline")}>{t("Show view")}</Button>}
    {state.kind === "opening" && <p className="mcp-app-notice">{t("Opening the view…")}</p>}
    {state.kind === "failed" && <p className="mcp-app-notice" role="status">{t("The view could not be opened: {error}", { error: state.error })}
      {" "}<Button size="small" variant="minimal" onClick={() => void open("inline")}>{t("Show view")}</Button></p>}
    {state.kind === "inline" && <McpAppFrame host={host} view={state.view} displayMode="inline" onDisplayMode={displayMode}
      closing={state.closing} onClosed={closedInline} />}
    {state.kind === "window" && <>
      <p className="mcp-app-notice">{t("Shown in a window")}</p>
      <AppWindow storageKey="mcp-app-window" titleId={titleId} title={label} className="mcp-app-window"
        preferredSize={viewport => ({ width: Math.round(viewport.width * 0.8), height: Math.round(viewport.height * 0.8) })}
        closeLabel={t("Close the view of {server}", { server })} onClose={closeWindow}
        onCancel={event => { event.preventDefault(); closeWindow(); }}>
        {state.view ? <McpAppFrame host={host} view={state.view} displayMode="fullscreen" onDisplayMode={displayMode}
          closing={state.closing ? "The window of the view closes." : null} onClosed={closedWindow} />
          : <p className="mcp-app-notice">{t("Opening the view…")}</p>}
      </AppWindow>
    </>}
  </div>;
}
