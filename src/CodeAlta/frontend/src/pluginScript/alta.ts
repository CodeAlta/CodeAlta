import { safeMarkdownHref } from "../markdownLinks";
import { altaInterfaceVersion, lentVersions } from "./versions";

/** A value that changes, and tells when: `value` is the current one and `subscribe` calls the listener with each new one, until the function it returns is called. */
export type AltaSignal<T> = Readonly<{ readonly value: T; subscribe(listener: (value: T) => void): () => void }>;

/** What a script is about: the plugin and the canvas it belongs to, and where its content is shown. Null for what does not apply. */
export type AltaContext = Readonly<{
  pluginKey: string; canvasId: string | null; instanceId: string | null; spaceId: string | null; projectId: string | null; sessionId: string | null; key: string | null;
  /** The input the canvas was opened with (parsed JSON), or null. */
  input: unknown;
}>;

/** The colors of the window as values, for what a script paints itself (a `<canvas>`, an SVG): the ones a chart draws with. */
export type AltaTheme = Readonly<{
  dark: boolean; fontFamily: string; text: string; muted: string; axis: string; grid: string; surface: string; tooltipBackground: string; tooltipBorder: string;
  /** The colors of the series, in order. */
  series: readonly string[];
  /** A sequential ramp from the lightest to the strongest. */
  ramp: readonly string[];
  /** The low, middle and high colors of a diverging scale. */
  diverging: readonly [string, string, string];
}>;

/** What a script asks of the window. A request the window cannot serve here (no host, no such command) does nothing, and never throws. */
export type AltaHost = Readonly<{
  /** Opens a file in the code editor. A relative path starts from the project of the context. */
  openFile(path: string, options?: Readonly<{ line?: number; column?: number }>): void;
  /** Opens the changes of a project (its diff view). */
  openDiff(options?: Readonly<{ projectId?: string }>): void;
  /** Opens a session of the window. */
  openSession(sessionId: string): void;
  /** Opens a canvas, of this plugin unless `pluginKey` names another one. */
  openCanvas(canvasId: string, options?: Readonly<{ pluginKey?: string; projectId?: string; sessionId?: string; key?: string }>): void;
  /** Opens a page of the web (http or https) in the system browser. */
  openLink(url: string): void;
  /** Shows a message of the window. */
  notify(message: string, options?: Readonly<{ tone?: "info" | "success" | "warning" | "danger" }>): void;
  /** Runs a command of this plugin, by its name, for the project and the session of the context. */
  runCommand(name: string): void;
  /** Sets the title of the tab; null brings back the one the plugin gave. */
  setTitle(title: string | null): void;
  /** Sets the status text shown beside the title of the tab; null clears it. */
  setStatus(status: string | null): void;
  /** Sets a short mark on the tab (a count, a word); null clears it. */
  setBadge(badge: string | number | null): void;
}>;

/**
 * The calls, the streams and the events of the plugin's own handlers (`canvas.Rpc` in C#). Every failure is an {@link AltaError} with a stable
 * `code` and a `retryable` mark; a call is never replayed by the window.
 */
export type AltaRpc = Readonly<{
  /** Calls a handler of the plugin and gives its result. The input is any JSON value; without one the handler reads `{}`. */
  invoke(name: string, input?: unknown, options?: Readonly<{ signal?: AbortSignal }>): Promise<unknown>;
  /** Calls a handler that answers with a stream, and gives its items one at a time. Stopping the iteration, or aborting the signal, ends the stream on the plugin's side. */
  stream(name: string, input?: unknown, options?: Readonly<{ signal?: AbortSignal }>): Promise<AsyncIterable<unknown>>;
  /** Listens to an event that the plugin sends (`PublishAsync`). Resolves once the plugin has the subscription; the function it gives stops the listening. Events sent before are not kept. */
  subscribe(name: string, handler: (value: unknown) => void, options?: Readonly<{ signal?: AbortSignal }>): Promise<() => void>;
  /** Counts the times the connection to the plugin was made again after it ended (the plugin was reloaded, the page reconnected): what was fetched before is stale. */
  generation: AltaSignal<number>;
}>;

/** What a script of a plugin is given: the one object through which it reaches the window, so that it touches no global. */
export type Alta = Readonly<{
  context: AltaContext;
  /** Whether the content is shown; false while its tab is behind another one. A script pauses its timers and its reads while it is. */
  visible: AltaSignal<boolean>;
  /** Aborts when the content goes away (the tab closed, the plugin reloaded): end what the script started. */
  closed: AbortSignal;
  host: AltaHost;
  theme: AltaSignal<AltaTheme>;
  /** Cleans a string of HTML, the way the fragments of plugins are: what is left can be assigned to `innerHTML`. */
  html(text: string): string;
  rpc: AltaRpc;
  /** The version of this interface and the versions of the libraries the window lends. */
  versions: Readonly<Record<string, string | number>>;
}>;

/** The window's side of what `alta.host` does: each member the window cannot do is left out. */
export type AltaHostBridge = Readonly<Partial<{
  openFile: (path: string, line: number | null, scope: Readonly<{ projectId: string | null; sessionId: string | null }>) => void;
  openDiff: (projectId: string | null) => void;
  openSession: (sessionId: string) => void;
  openCanvas: (request: Readonly<{ pluginKey: string; canvasId: string; projectId: string | null; sessionId: string | null; key: string | null }>) => void;
  openLink: (url: string) => void;
  notify: (message: string, tone: "info" | "success" | "warning" | "danger") => void;
  runCommand: (name: string) => void;
  setTitle: (title: string | null) => void;
  setStatus: (status: string | null) => void;
  setBadge: (badge: string | null) => void;
}>>;

/** What `createAlta` needs from the window. */
export type AltaOptions = Readonly<{
  context: AltaContext;
  visible: boolean;
  host: AltaHostBridge;
  sanitize: (html: string) => string;
  readTheme: () => AltaTheme;
  subscribeTheme: (listener: () => void) => () => void;
  /** The calls of the plugin of the script, when the window carries them (the script of a canvas); scripts otherwise get the error `rpc_unavailable`. */
  rpc?: AltaRpc;
}>;

/** What the window keeps of an `alta` object it made: the object, how to tell it the tab is shown or hidden, and how to end it. */
export type AltaHandle = Readonly<{ alta: Alta; setVisible(visible: boolean): void; dispose(): void }>;

/**
 * The error of a call of `alta.rpc`, or of any request a script makes that the window cannot serve. `code` is stable: the ones of a plugin
 * (`PluginRpcException`), the ones of the transport (`connection_closed`, `timeout`, `too_many_requests`, `payload_too_large`, `operation_canceled`,
 * `command_not_found`, `invalid_request`, `internal_error`) and `rpc_unavailable`. `retryable` says whether the same call can succeed later.
 */
export class AltaError extends Error {
  readonly retryable: boolean;
  readonly correlationId?: string;

  constructor(readonly code: string, message: string, options?: Readonly<{ retryable?: boolean; correlationId?: string }>) {
    super(message);
    this.name = "AltaError";
    this.retryable = options?.retryable ?? false;
    this.correlationId = options?.correlationId;
  }
}

const noGeneration: AltaSignal<number> = Object.freeze({ value: 0, subscribe: () => () => { } });
const unavailable = () => Promise.reject(new AltaError("rpc_unavailable", "alta.rpc is not available here: only the script of a canvas can call its plugin."));
const unavailableRpc: AltaRpc = Object.freeze({ invoke: unavailable, stream: unavailable, subscribe: unavailable, generation: noGeneration });

function textOf(value: unknown, limit: number): string | null {
  return typeof value === "string" && value.length > 0 && value.length <= limit && !/[\u0000-\u001f\u007f]/u.test(value) ? value : null;
}

function placeOf(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 1 && value <= 10_000_000 ? value : null;
}

/**
 * Makes the `alta` object of one script: what it is about, whether its content is shown, when it goes away, and what it can ask of
 * the window. The object is frozen, and what a script passes in is checked: a bad value does nothing.
 */
export function createAlta(options: AltaOptions): AltaHandle {
  const controller = new AbortController();
  const listeners = new Set<(value: boolean) => void>();
  let visible = options.visible;
  const visibleSignal: AltaSignal<boolean> = {
    get value() { return visible; },
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  };
  const themeListeners = new Set<(value: AltaTheme) => void>();
  let theme = options.readTheme();
  let unsubscribeTheme: (() => void) | null = null;
  const themeSignal: AltaSignal<AltaTheme> = {
    get value() { return theme; },
    subscribe(listener) {
      // The window is watched only while a script listens.
      if (themeListeners.size === 0 && !controller.signal.aborted) unsubscribeTheme = options.subscribeTheme(() => {
        theme = options.readTheme();
        for (const each of [...themeListeners]) each(theme);
      });
      themeListeners.add(listener);
      return () => {
        themeListeners.delete(listener);
        if (themeListeners.size === 0) { unsubscribeTheme?.(); unsubscribeTheme = null; }
      };
    },
  };
  const { context, host } = options;
  const scope = { projectId: context.projectId, sessionId: context.sessionId };
  const hostApi: AltaHost = Object.freeze({
    openFile(path, place) {
      const text = textOf(path, 2048);
      if (text) host.openFile?.(text, placeOf(place?.line), scope);
    },
    openDiff(place) { host.openDiff?.(textOf(place?.projectId, 256) ?? context.projectId); },
    openSession(sessionId) { const id = textOf(sessionId, 128); if (id) host.openSession?.(id); },
    openCanvas(canvasId, place) {
      const id = textOf(canvasId, 64);
      if (!id || !/^[A-Za-z0-9._-]{1,64}$/u.test(id)) return;
      host.openCanvas?.({ pluginKey: textOf(place?.pluginKey, 512) ?? context.pluginKey, canvasId: id,
        projectId: textOf(place?.projectId, 256) ?? context.projectId, sessionId: textOf(place?.sessionId, 128) ?? context.sessionId, key: textOf(place?.key, 128) });
    },
    openLink(url) { if (typeof url === "string" && safeMarkdownHref(url)) host.openLink?.(url); },
    notify(message, place) {
      const text = textOf(message, 1000);
      const tone = place?.tone === "success" || place?.tone === "warning" || place?.tone === "danger" ? place.tone : "info";
      if (text) host.notify?.(text, tone);
    },
    runCommand(name) { const command = textOf(name, 128); if (command) host.runCommand?.(command); },
    setTitle(title) { host.setTitle?.(title === null ? null : textOf(title, 200)); },
    setStatus(status) { host.setStatus?.(status === null ? null : textOf(status, 200)); },
    setBadge(badge) {
      const text = typeof badge === "number" && Number.isFinite(badge) ? String(Math.trunc(badge)).slice(0, 8) : textOf(badge, 8);
      if (badge === null || text !== null) host.setBadge?.(text);
    },
  });
  const alta: Alta = Object.freeze({
    context: Object.freeze({ ...context }),
    visible: visibleSignal,
    closed: controller.signal,
    host: hostApi,
    theme: themeSignal,
    html: (text: string) => options.sanitize(String(text ?? "")),
    rpc: options.rpc ?? unavailableRpc,
    versions: Object.freeze({ interface: altaInterfaceVersion, ...lentVersions }),
  });
  return {
    alta,
    setVisible(next) {
      if (visible === next || controller.signal.aborted) return;
      visible = next;
      for (const listener of [...listeners]) listener(next);
    },
    dispose() {
      if (controller.signal.aborted) return;
      controller.abort();
      listeners.clear();
      themeListeners.clear();
      unsubscribeTheme?.();
      unsubscribeTheme = null;
    },
  };
}
