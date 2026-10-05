import { useContext, useEffect, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { InputGroup } from "@blueprintjs/core";
import { pluginUi, type PluginUiPickerResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { usePromptPicker } from "./promptPicker";
import { activePluginReference, insertPluginReference, PluginUiContext, type PluginPickerView } from "./pluginUi";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

const pageStep = 8;

/** The prompt pickers that plugins contribute for the composer's project: one per trigger character. */
export function PluginPromptPickers({ edit, input, sessionId }: { edit: (text: string) => void; input: RefObject<PromptInput | null>; sessionId: string | null }) {
  const { contributions } = useContext(PluginUiContext);
  return <>{contributions.pickers.map(picker => <PluginPromptPicker key={picker.id} picker={picker} edit={edit} input={input} sessionId={sessionId} />)}</>;
}

/**
 * One plugin picker of a prompt editor. Typing its trigger character at a word start opens a search
 * window; the plugin supplies the items for the text typed after it. Enter replaces the token with the
 * text of the selected item, Escape leaves the prompt as typed.
 */
function PluginPromptPicker({ picker, edit, input, sessionId, api = pluginUi }: {
  picker: PluginPickerView; edit: (text: string) => void; input: RefObject<PromptInput | null>; sessionId: string | null; api?: Pick<typeof pluginUi, "searchPicker">;
}) {
  const { t } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const latest = useRef({ scope, sessionId }); latest.current = { scope, sessionId };
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [page, setPage] = useState<PluginUiPickerResponse | null>(null);
  const [failed, setFailed] = useState(false);
  const { trigger, dialog, close } = usePromptPicker({ input, edit, enabled: !!scope, focus: search,
    detect: (text, caret) => activePluginReference(picker.trigger, text, caret),
    onOpen: value => { setQuery(value.query); setSelected(0); setPage(null); setFailed(false); } });
  const open = !!trigger;

  useEffect(() => {
    const current = latest.current;
    if (!open || !current.scope) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void api.searchPicker({ expectedEpoch: current.scope!.expectedEpoch, pickerId: picker.id, projectId: current.scope!.projectId, sessionId: current.sessionId, query },
        { signal: controller.signal, timeoutMilliseconds: 20_000 }).then(value => {
        if (controller.signal.aborted) return;
        setFailed(value.status !== "ok"); setPage(value); setSelected(0);
      }).catch(() => { if (!controller.signal.aborted) { setFailed(true); setPage(null); } });
    }, page ? 220 : 0);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [open, query]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, page]);

  const items = page?.status === "ok" ? page.items : [];
  const count = items.length;
  const index = Math.max(0, Math.min(selected, count - 1));
  const move = (delta: number) => setSelected(Math.max(0, Math.min(count - 1, index + delta)));
  function choose(at: number) {
    const item = items[at];
    const element = input.current;
    if (!item || !trigger || !element || element.value !== trigger.text) return;
    const next = insertPluginReference(trigger.text, trigger.start, trigger.end, item.insertText);
    if (next) close(next);
  }
  const loading = !page && !failed;
  return trigger && <dialog ref={dialog} className="app-dialog reference-palette" aria-modal="true" aria-labelledby={`${listId}-title`}
    onCancel={event => { event.preventDefault(); close(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.metaKey || event.ctrlKey || event.altKey) return;
      const handled = event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (move(pageStep), true) : event.key === "PageUp" ? (move(-pageStep), true)
        : event.key === "Enter" ? (choose(index), true) : event.key === "Escape" ? (close(), true) : false;
      if (handled) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.plugin-picker.v1" titleId={`${listId}-title`}
      title={<><AppIcon name="plugin" size={14} /> {picker.title}<span className="reference-project"> · {picker.plugin}</span></>}
      preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(480, viewport.height - 40) })}
      minimumSize={{ width: 420, height: 240 }} onClose={() => close()} closeLabel={t("Close")}
      headerActions={loading && <span className="reference-status" role="status"><ActivitySpinner size={12} />{t("Loading…")}</span>}>
      <InputGroup inputRef={search} className="reference-search" type="search" maxLength={256} value={query} spellCheck={false}
        leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={picker.title}
        role="combobox" aria-expanded="true" aria-controls={listId} aria-label={picker.title}
        aria-activedescendant={items[index] ? `${listId}-${index}` : undefined}
        onChange={event => { setQuery(event.target.value.replace(/\s/gu, "")); setSelected(0); }} />
      <div id={listId} ref={list} role="listbox" aria-label={picker.title} className="reference-list">
        {items.map((item, at) => <div role="option" id={`${listId}-${at}`} key={at} aria-selected={at === index} className="plugin-picker-row" title={item.insertText}
          onMouseMove={() => { if (at !== index) setSelected(at); }} onClick={() => choose(at)}>
          <span className="plugin-picker-label">{item.label}</span>
          {item.description && <span className="plugin-picker-description">{item.description}</span>}
        </div>)}
        {failed && <p className="reference-empty">{t("The items could not be loaded.")}</p>}
        {!failed && page && count === 0 && <p className="reference-empty">{t("No item matches.")}</p>}
      </div>
      <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("insert")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
    </AppWindowSurface>
  </dialog>;
}
