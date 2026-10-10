import { createElement, Fragment, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { MarkdownContent } from "../MarkdownContent";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import { AltaError, type Alta, type AltaSignal, type AltaTheme } from "./alta";
import { createHtml } from "./html";
import { useAlta } from "./PluginScript";
import { retryDelay, retryLimit } from "./retry";

/**
 * The `html` tag of the window's React: JSX without a build step. See {@link createHtml}.
 *
 * ```js
 * import { html } from "codealta";
 * html`<${Button} onClick=${save}>Save<//>`
 * ```
 */
export const html = createHtml(createElement as never, Fragment);

function useSignal<T>(signal: AltaSignal<T>): T {
  return useSyncExternalStore(signal.subscribe, () => signal.value);
}

/** Whether the content is shown: false while its tab is behind another one. A component pauses its timers and its reads while it is. */
export function useVisible(): boolean {
  return useSignal(useAlta().visible);
}

/** The colors of the window as values; the component draws again when the theme or the color scheme changes. */
export function useTheme(): AltaTheme {
  return useSignal(useAlta().theme);
}

/** What a call of the plugin gave: `loading` while it runs, then the `data` or the `error`; `reload` calls again. */
export type RpcResult<T> = Readonly<{ data: T | undefined; error: Error | null; loading: boolean; reload: () => void }>;

/**
 * Calls a handler of the plugin when the component is drawn and shown, and again when the name or the input change, `reload` is called, the
 * tab is shown again after it was hidden, or the connection to the plugin was made again (the data may be stale). A hidden component asks for
 * nothing. The call is dropped when the component goes away. Needs `alta.rpc`: where the window does not carry it, the result is the error
 * `rpc_unavailable`. A call that failed because the connection ended is asked again a few times, after a wait that doubles from half a second;
 * then it keeps its error until one of the reasons above asks again.
 */
export function useRpc<T = unknown>(name: string, input?: unknown): RpcResult<T> {
  const alta = useAlta();
  const visible = useVisible();
  const generation = useSignal(alta.rpc.generation);
  const [version, ask] = useReducer((value: number) => value + 1, 0);
  const [attempt, again] = useReducer((value: number) => value + 1, 0);
  const [state, setState] = useState<Readonly<{ data: T | undefined; error: Error | null; loading: boolean }>>({ data: undefined, error: null, loading: true });
  const inputKey = JSON.stringify(input ?? null);
  // The tries made in a row that the connection failed: a new reason to ask starts the count again, a try of its own does not.
  const failures = useRef(0);
  useEffect(() => { failures.current = 0; }, [alta, name, inputKey, version, visible, generation]);
  const reload = useCallback(() => ask(), []);
  useEffect(() => {
    if (!visible) return undefined;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    setState(previous => ({ ...previous, loading: true }));
    alta.rpc.invoke(name, JSON.parse(inputKey), { signal: controller.signal }).then(
      data => {
        if (controller.signal.aborted) return;
        failures.current = 0;
        setState({ data: data as T, error: null, loading: false });
      },
      error => {
        if (controller.signal.aborted) return;
        const failure = error instanceof Error ? error : new AltaError("rpc_failed", String(error));
        setState(previous => ({ data: previous.data, error: failure, loading: false }));
        if (!(failure instanceof AltaError) || !failure.retryable || failure.code !== "connection_closed") return;
        const delay = retryDelay(failures.current, retryLimit);
        if (delay === null) return;
        failures.current++;
        timer = setTimeout(again, delay);
      });
    return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [alta, name, inputKey, version, visible, generation, attempt]);
  return useMemo(() => ({ ...state, reload }), [state, reload]);
}

/** What a stream of the plugin gave: the `latest` item and every item so far (`items`, the last 1000), and whether it is still `active`. */
export type StreamResult<T> = Readonly<{ latest: T | undefined; items: readonly T[]; error: Error | null; active: boolean }>;

/**
 * Follows a stream of the plugin while the component is drawn and shown: a hidden component lets go of the stream and takes it again when it is
 * shown. A stream that ended because the connection to the plugin ended is taken again after a short wait, longer each time. Needs `alta.rpc`:
 * where the window does not carry it, the result is the error `rpc_unavailable`.
 */
export function useStream<T = unknown>(name: string, input?: unknown): StreamResult<T> {
  const alta = useAlta();
  const visible = useVisible();
  const [state, setState] = useState<StreamResult<T>>({ latest: undefined, items: [], error: null, active: false });
  const [attempt, again] = useReducer((value: number) => value + 1, 0);
  const inputKey = JSON.stringify(input ?? null);
  useEffect(() => {
    if (!visible) { setState(previous => ({ ...previous, active: false })); return undefined; }
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    setState(previous => ({ ...previous, error: null, active: true }));
    void (async () => {
      try {
        const stream = await alta.rpc.stream(name, JSON.parse(inputKey), { signal: controller.signal });
        for await (const item of stream) {
          if (controller.signal.aborted) return;
          setState(previous => ({ latest: item as T, items: [...previous.items, item as T].slice(-1000), error: null, active: true }));
        }

        if (!controller.signal.aborted) setState(previous => ({ ...previous, active: false }));
      } catch (error) {
        if (controller.signal.aborted) return;
        const failure = error instanceof Error ? error : new AltaError("rpc_failed", String(error));
        setState(previous => ({ ...previous, error: failure, active: false }));
        if (failure instanceof AltaError && failure.retryable && failure.code === "connection_closed") {
          timer = setTimeout(again, retryDelay(attempt) ?? 0);
        }
      }
    })();
    return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [alta, name, inputKey, visible, attempt]);
  return state;
}

/** A text of Markdown, drawn as the messages of a session are: headings, lists, tables, colored code and diagrams. */
export function Markdown({ source }: { source: string }) {
  return <MarkdownContent source={source} timelineCodeBlocks />;
}

// A fence longer than any run of backticks in the text, so that the text cannot close it.
function fence(text: string, language: string | null): string {
  const longest = Math.max(0, ...(text.match(/`+/gu) ?? []).map(run => run.length));
  const marks = "`".repeat(Math.max(3, longest + 1));
  return `${marks}${language ?? ""}\n${text.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "")}\n${marks}`;
}

/** Source code, colored for its language (`csharp`, `json`, `diff`…; plain text when it is not known). */
export function Code({ code, language }: { code: string; language?: string }) {
  return <MarkdownContent source={fence(code, language && /^[A-Za-z0-9_-]{1,32}$/u.test(language) ? language : null)} timelineCodeBlocks />;
}

/** A diagram from its Mermaid text, in the colors of the window. */
export function Diagram({ source }: { source: string }) {
  return <MarkdownContent source={fence(source, "mermaid")} timelineCodeBlocks />;
}

/** A link that opens a file in the code editor, at a line when it has one. Its text is its children, or the path. */
export function FileLink({ path, line, children }: { path: string; line?: number; children?: ReactNode }) {
  const alta = useAlta();
  const label = children ?? (line ? `${path}:${line}` : path);
  return <a className="plugin-file-link" href="#" title={path} onClick={event => { event.preventDefault(); alta.host.openFile(path, line ? { line } : undefined); }}>{label}</a>;
}

/** A link that opens a session of the window. Its text is its children, or the title of the session. */
export function SessionLink({ sessionId, children }: { sessionId: string; children?: ReactNode }) {
  const alta: Alta = useAlta();
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  const title = links?.title(sessionId) ?? sessionId.slice(0, 8);
  return <a className="plugin-session-link" href="#" title={t("Open the session {title}", { title })}
    onClick={event => { event.preventDefault(); alta.host.openSession(sessionId); }}>{children ?? title}</a>;
}
