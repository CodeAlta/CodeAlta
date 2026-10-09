import { useEffect, useRef, useState } from "react";
import { Button, InputGroup, TextArea } from "@blueprintjs/core";
import { pluginUi, type PluginUiAnswer, type PluginUiEvent } from "#neoastra";
import { AppWindow } from "./AppWindow";
import { showToast } from "./appToaster";
import { PluginHtml } from "./PluginHtml";
import { collectPluginFields } from "./pluginHtmlSanitizer";
import { pluginsChangedEvent } from "./pluginUi";
import { useShellLanguage } from "./shellLanguage";

type Api = Pick<typeof pluginUi, "watch" | "respond" | "dialogAction">;
type Ask = PluginUiEvent & { requestId: string };
const reconnectMilliseconds = 2000;

/**
 * Shows what plugins ask of the window: a notification as a toast, a dialog as a window of the application,
 * and a prompt handed to the composer of its session. One instance watches the host for the whole window.
 *
 * `onPrompt` sends, queues or steers a prompt, or compacts, and resolves to whether the composer took it;
 * `onDraft` replaces a prompt draft.
 */
export function PluginUiHost({ epoch, onPrompt, onDraft, api = pluginUi }: {
  epoch: string | null; onPrompt: (request: PluginUiEvent) => boolean; onDraft: (request: PluginUiEvent) => void; api?: Api;
}) {
  const [asks, setAsks] = useState<readonly Ask[]>([]);
  const handlers = useRef({ onPrompt, onDraft }); handlers.current = { onPrompt, onDraft };
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    let timer: number | undefined;
    const answer = (value: PluginUiAnswer) => void api.respond(value, { timeoutMilliseconds: 8000 }).catch(() => { /* The plugin times out on its own. */ });
    async function watch() {
      try {
        for await (const event of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) {
          if (abort.signal.aborted) return;
          const requestId = event.requestId;
          if (event.kind === "notify" && event.message) {
            showToast({ message: event.message, intent: event.tone === "warning" ? "warning" : "none", icon: event.tone === "warning" ? "warning-sign" : "info-sign", timeout: 8000 });
          } else if (event.kind === "ask" && requestId) {
            setAsks(current => current.some(ask => ask.requestId === requestId) ? current : [...current, { ...event, requestId }]);
          } else if (event.kind === "close" && requestId) {
            setAsks(current => current.filter(ask => ask.requestId !== requestId));
          } else if (event.kind === "prompt" && requestId) {
            let taken = false;
            try { taken = handlers.current.onPrompt(event); } catch { taken = false; }
            answer({ requestId, button: taken ? "ok" : null, cancelled: !taken, text: null, selectedIndex: null, values: null });
          } else if (event.kind === "draft") {
            handlers.current.onDraft(event);
          } else if (event.kind === "refresh") {
            // A command or a dialog action of a plugin ended: its status items and its content follow at once.
            window.dispatchEvent(new Event(pluginsChangedEvent));
          }
        }
      } catch { /* The channel ended: watch again below, unless the window is going away. */ }
      if (!abort.signal.aborted) timer = window.setTimeout(() => void watch(), reconnectMilliseconds);
    }
    void watch();
    return () => { abort.abort(); window.clearTimeout(timer); setAsks([]); };
  }, [epoch, api]);

  const ask = asks[0];
  if (!ask) return null;
  const finish = (value: Omit<PluginUiAnswer, "requestId">) => {
    setAsks(current => current.filter(item => item.requestId !== ask.requestId));
    void api.respond({ requestId: ask.requestId, ...value }, { timeoutMilliseconds: 8000 }).catch(() => { /* The request was withdrawn. */ });
  };
  return <PluginDialog key={ask.requestId} ask={ask} api={api} onFinish={finish} onClosed={() => setAsks(current => current.filter(item => item.requestId !== ask.requestId))} />;
}

/** One dialog asked by a plugin. Dismissing it answers as cancelled. */
function PluginDialog({ ask, api, onFinish, onClosed }: {
  ask: Ask; api: Api; onFinish: (answer: Omit<PluginUiAnswer, "requestId">) => void; onClosed: () => void;
}) {
  const { t } = useShellLanguage();
  const kind = ask.dialog ?? "message";
  const [text, setText] = useState(ask.text ?? "");
  const [selected, setSelected] = useState(() => Math.max(0, ask.items?.findIndex(item => item.selected) ?? 0));
  const [html, setHtml] = useState(ask.html ?? "");
  const [busy, setBusy] = useState(false);
  const content = useRef<HTMLDivElement>(null);
  const first = useRef<HTMLElement | null>(null);
  const titleId = `plugin-dialog-${ask.requestId}`;
  const editing = kind === "input" || kind === "edit";
  const buttons = ask.buttons?.length ? ask.buttons
    : kind === "message" || kind === "html" ? [{ name: "close", label: t("Close"), isDefault: true, isCancel: true }]
    : kind === "confirm" ? [{ name: "yes", label: t("Yes"), isDefault: true, isCancel: false }, { name: "no", label: t("No"), isDefault: false, isCancel: true }]
    : [{ name: "ok", label: t("OK"), isDefault: true, isCancel: false }, { name: "cancel", label: t("Cancel"), isDefault: false, isCancel: true }];
  const values = () => kind === "html" && content.current ? collectPluginFields(content.current) : null;
  const cancel = () => onFinish({ button: null, cancelled: true, text: null, selectedIndex: null, values: values() });
  const press = (button: { name: string; isCancel: boolean }) => onFinish({
    button: button.name, cancelled: button.isCancel, text: editing && !button.isCancel ? text : null,
    selectedIndex: kind === "select" && !button.isCancel ? selected : null, values: values(),
  });
  const accept = () => { const button = buttons.find(item => item.isDefault && !item.isCancel) ?? buttons.find(item => !item.isCancel); if (button) press(button); };

  async function action(name: string, value: string | null, fields: Record<string, string>) {
    if (busy || !ask.actions) return;
    setBusy(true);
    try {
      const reply = await api.dialogAction({ requestId: ask.requestId, action: name, value, values: fields }, { timeoutMilliseconds: 120_000 });
      if (reply.closed) onClosed();
      else if (reply.status === "ok" && typeof reply.html === "string") setHtml(reply.html);
      else if (reply.status === "unknown") onClosed();
    } catch { /* The dialog stays as it is. */ }
    finally { setBusy(false); }
  }

  // The focus starts where the answer is given: the field of the fragment, the text (selected, so typing
  // replaces it), the list, or the default button.
  function opened() {
    const field = kind === "html" ? content.current?.querySelector<HTMLElement>("input:not([type=hidden]):not(:disabled), select:not(:disabled), textarea:not(:disabled)") : null;
    const target = field ?? first.current;
    target?.focus();
    if (kind === "input" && target instanceof HTMLInputElement) target.select();
  }
  useEffect(() => {
    if (kind === "select") document.getElementById(`${titleId}-${selected}`)?.scrollIntoView({ block: "nearest" });
  }, [kind, titleId, selected]);

  const sizes: Readonly<Record<string, [number, number]>> = { message: [520, 240], confirm: [520, 240], input: [520, 220], edit: [760, 520], select: [560, 460], html: [720, 520] };
  const [width, height] = sizes[kind] ?? sizes.message;
  return <AppWindow storageKey={`codealta.desktop.window.plugin-${kind}.v1`} className="plugin-dialog" titleId={titleId} title={ask.title ?? t("Plugin")}
    preferredSize={viewport => ({ width: Math.min(width, viewport.width - 40), height: Math.min(height, viewport.height - 40) })}
    minimumSize={{ width: 320, height: 180 }} onClose={cancel} closeLabel={t("Close")} onOpened={opened}
    // A plugin that asks waits for the answer; only a message has nothing to lose.
    keepOnOutsidePress={kind !== "message"}
    onCancel={event => { event.preventDefault(); cancel(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      if (event.key === "Escape") { event.preventDefault(); cancel(); }
      else if (kind === "select" && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
        event.preventDefault();
        setSelected(current => Math.max(0, Math.min((ask.items?.length ?? 1) - 1, current + (event.key === "ArrowDown" ? 1 : -1))));
      } else if (event.key === "Enter" && !event.shiftKey && (kind === "select" || kind === "input" || kind === "confirm" || kind === "message" || event.ctrlKey && kind === "edit")
        && !(event.target as HTMLElement).closest("button")) { event.preventDefault(); accept(); }
    }}>
    <div className="plugin-dialog-body" data-kind={kind} aria-busy={busy}>
      {ask.message && <p className="plugin-dialog-message">{ask.message}</p>}
      {kind === "input" && <InputGroup inputRef={element => { first.current = element; }} value={text} aria-label={ask.title ?? ""} onChange={event => setText(event.target.value)} />}
      {kind === "edit" && <TextArea inputRef={element => { first.current = element; }} className="plugin-dialog-editor" fill value={text} spellCheck={false}
        aria-label={ask.title ?? ""} onChange={event => setText(event.target.value)} />}
      {kind === "select" && <div className="plugin-dialog-list" role="listbox" tabIndex={0} ref={element => { first.current = element; }} aria-label={ask.title ?? ""}
        aria-activedescendant={`${titleId}-${selected}`}>
        {(ask.items ?? []).map((item, index) => <div key={index} id={`${titleId}-${index}`} role="option" aria-selected={index === selected} className="plugin-dialog-option"
          onClick={() => setSelected(index)} onDoubleClick={() => { setSelected(index); onFinish({ button: "ok", cancelled: false, text: null, selectedIndex: index, values: null }); }}>
          <strong>{item.label}</strong>{item.description && <small>{item.description}</small>}</div>)}
      </div>}
      {kind === "html" && <PluginHtml ref={content} html={html} className="plugin-dialog-html" onAction={(name, value, fields) => void action(name, value, fields)} onSubmit={accept} />}
    </div>
    <footer className="plugin-dialog-buttons">
      {buttons.map(button => <Button key={button.name} intent={button.isDefault && !button.isCancel ? "primary" : "none"} disabled={busy}
        ref={kind === "message" || kind === "confirm" || kind === "html" ? element => { if (button.isDefault || !first.current) first.current = element; } : undefined}
        onClick={() => press(button)}>{button.label}</Button>)}
    </footer>
  </AppWindow>;
}
