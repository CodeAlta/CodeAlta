import { createElement, Fragment, useContext, useEffect, useMemo, useReducer, useState, useSyncExternalStore, type ReactNode } from "react";
import { MarkdownContent } from "../MarkdownContent";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import { AltaError, type Alta, type AltaSignal, type AltaTheme } from "./alta";
import { createHtml } from "./html";
import { useAlta } from "./PluginScript";

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
 * Calls a handler of the plugin when the component is drawn, and again when the name or the input change or `reload` is called. The
 * call is dropped when the component goes away. Needs `alta.rpc`: until the window carries it, the result is the error `rpc_unavailable`.
 */
export function useRpc<T = unknown>(name: string, input?: unknown): RpcResult<T> {
  const alta = useAlta();
  const [version, reload] = useReducer((value: number) => value + 1, 0);
  const [state, setState] = useState<Readonly<{ data: T | undefined; error: Error | null; loading: boolean }>>({ data: undefined, error: null, loading: true });
  const inputKey = JSON.stringify(input ?? null);
  useEffect(() => {
    const controller = new AbortController();
    setState(previous => ({ ...previous, loading: true }));
    alta.rpc.invoke(name, JSON.parse(inputKey), { signal: controller.signal }).then(
      data => { if (!controller.signal.aborted) setState({ data: data as T, error: null, loading: false }); },
      error => { if (!controller.signal.aborted) setState(previous => ({ data: previous.data, error: error instanceof Error ? error : new AltaError("rpc_failed", String(error)), loading: false })); });
    return () => controller.abort();
  }, [alta, name, inputKey, version]);
  return useMemo(() => ({ ...state, reload }), [state]);
}

/** What a stream of the plugin gave: the `latest` item and every item so far (`items`, the last 1000), and whether it is still `active`. */
export type StreamResult<T> = Readonly<{ latest: T | undefined; items: readonly T[]; error: Error | null; active: boolean }>;

/**
 * Follows a stream of the plugin while the component is drawn and shown: a hidden component lets go of the stream and takes it again when it is
 * shown. Needs `alta.rpc`: until the window carries it, the result is the error `rpc_unavailable`.
 */
export function useStream<T = unknown>(name: string, input?: unknown): StreamResult<T> {
  const alta = useAlta();
  const visible = useVisible();
  const [state, setState] = useState<StreamResult<T>>({ latest: undefined, items: [], error: null, active: false });
  const inputKey = JSON.stringify(input ?? null);
  useEffect(() => {
    if (!visible) { setState(previous => ({ ...previous, active: false })); return undefined; }
    const controller = new AbortController();
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
        if (!controller.signal.aborted) setState(previous => ({ ...previous, error: error instanceof Error ? error : new AltaError("rpc_failed", String(error)), active: false }));
      }
    })();
    return () => controller.abort();
  }, [alta, name, inputKey, visible]);
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
