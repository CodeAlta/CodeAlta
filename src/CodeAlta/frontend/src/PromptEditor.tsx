import { useImperativeHandle, useLayoutEffect, useRef, type ClipboardEventHandler, type KeyboardEventHandler, type Ref } from "react";
import { Classes } from "@blueprintjs/core";
import "monaco-editor/languages/definitions/markdown/register.js";
import { followShellTheme, monaco } from "./monacoEnvironment";


export type PromptInput = Pick<HTMLTextAreaElement, "value" | "disabled" | "selectionStart" | "selectionEnd"
  | "isConnected" | "focus" | "contains" | "closest" | "setSelectionRange" | "addEventListener" | "removeEventListener">;

export function PromptEditor({ ref, value, onChange, disabled = false, expanded = false, id, label, placeholder,
  onPaste, onKeyDown, onCompositionStart }: {
  ref?: Ref<PromptInput>; value: string; onChange: (text: string) => void; disabled?: boolean; expanded?: boolean;
  id?: string; label: string; placeholder?: string; onPaste?: ClipboardEventHandler<HTMLElement>;
  onKeyDown?: KeyboardEventHandler<HTMLDivElement>; onCompositionStart?: () => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor>(null);
  const latest = useRef({ value, onChange, disabled }); latest.current = { value, onChange, disabled };
  useLayoutEffect(() => {
    const node = host.current!;
    const model = monaco.editor.createModel(latest.current.value, "markdown");
    const instance = monaco.editor.create(node.firstElementChild as HTMLElement, { model, automaticLayout: true, ariaLabel: label,
      readOnly: latest.current.disabled, fontFamily: getComputedStyle(node).fontFamily, fontSize: 13, lineHeight: 20,
      minimap: { enabled: false }, lineNumbers: "off", glyphMargin: false, folding: false,
      lineDecorationsWidth: 0, lineNumbersMinChars: 0, scrollBeyondLastLine: false, wordWrap: "on",
      overviewRulerLanes: 0, renderLineHighlight: "none", stickyScroll: { enabled: false },
      padding: { top: 8, bottom: 8 }, scrollbar: { alwaysConsumeMouseWheel: false },
      quickSuggestions: false, suggestOnTriggerCharacters: false, tabFocusMode: true, links: false });
    editor.current = instance;
    const unfollowTheme = followShellTheme();

    const size = () => { if (!expanded) node.style.height = `${Math.max(56, Math.min(240, instance.getContentHeight()))}px`; };
    const resized = instance.onDidContentSizeChange(size); size();
    let updating = false;
    const changed = model.onDidChangeContent(() => {
      if (updating) return;
      const next = model.getValue();
      if (next.length > 32768) {
        updating = true; instance.trigger("prompt", "undo", null); updating = false; return;
      }
      if (next !== latest.current.value) latest.current.onChange(next);
      node.dispatchEvent(new Event("input", { bubbles: true }));
    });
    const selection = instance.onDidChangeCursorSelection(() => node.dispatchEvent(new Event("select")));
    return () => { unfollowTheme(); changed.dispose(); selection.dispose(); resized.dispose(); instance.dispose(); model.dispose(); editor.current = null; };
  }, [expanded]);
  useImperativeHandle(ref, () => ({
    get value() { return editor.current?.getValue() ?? latest.current.value; },
    get disabled() { return latest.current.disabled; },
    get isConnected() { return host.current?.isConnected ?? false; },
    get selectionStart() { const e = editor.current; return e?.getModel()?.getOffsetAt(e.getSelection()!.getStartPosition()) ?? 0; },
    get selectionEnd() { const e = editor.current; return e?.getModel()?.getOffsetAt(e.getSelection()!.getEndPosition()) ?? 0; },
    focus: () => editor.current?.focus(), contains: node => host.current?.contains(node) ?? false,
    closest: ((selector: string) => host.current?.closest(selector) ?? null) as PromptInput["closest"],
    setSelectionRange: (start, end) => { const model = editor.current?.getModel(); if (!model) return;
      const a = model.getPositionAt(start ?? 0), b = model.getPositionAt(end ?? 0);
      editor.current?.setSelection(new monaco.Selection(a.lineNumber, a.column, b.lineNumber, b.column)); },
    addEventListener: (...args: Parameters<HTMLElement["addEventListener"]>) => host.current?.addEventListener(...args),
    removeEventListener: (...args: Parameters<HTMLElement["removeEventListener"]>) => host.current?.removeEventListener(...args),
  }), []);
  useLayoutEffect(() => {
    const instance = editor.current;
    if (!instance) return;
    instance.updateOptions({ readOnly: disabled, ariaLabel: label });
    if (instance.getValue() !== value) {
      const model = instance.getModel()!;
      instance.executeEdits("draft", [{ range: model.getFullModelRange(), text: value }]);
    }
  }, [value, disabled, label]);
  return <div id={id} ref={host} className={`prompt-editor ${Classes.MONOSPACE_TEXT}${expanded ? " expanded" : ""}`}
    tabIndex={-1} onFocus={event => { if (event.target === event.currentTarget) editor.current?.focus(); }}
    onKeyDownCapture={onKeyDown} onPasteCapture={event => { onPaste?.(event); if (event.defaultPrevented) event.stopPropagation(); }} onCompositionStart={onCompositionStart}
    aria-label={label} data-placeholder={!value ? placeholder : undefined}><div className="prompt-editor-surface" /></div>;
}
