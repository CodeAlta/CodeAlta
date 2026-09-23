import { useEffect, useRef } from "react";
import { dispatchExpandedComposerKey } from "./composerKeyboard";

export function ExpandedPromptEditor({ text, onChange, onClose }: {
  text: string; onChange: (text: string) => void; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const editor = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const element = dialog.current!;
    element.showModal();
    editor.current?.focus();
    return () => element.close();
  }, []);

  return <dialog ref={dialog} className="expanded-prompt-dialog" aria-labelledby="expanded-prompt-title"
    onCancel={event => { event.preventDefault(); onClose(); }} onKeyDown={event => {
      // Modal editing must never fall through to shell shortcuts or the regular composer's Send/Steer.
      event.stopPropagation();
      if (dispatchExpandedComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
        altKey: event.altKey, metaKey: event.metaKey, repeat: event.repeat,
        isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode,
        defaultPrevented: event.defaultPrevented }, onClose)) event.preventDefault();
      else if (event.key === "Escape") event.preventDefault();
    }}>
    <header><h2 id="expanded-prompt-title">Edit prompt</h2><button type="button" onClick={onClose}>Close</button></header>
    <textarea ref={editor} aria-label="Expanded prompt" aria-describedby="expanded-prompt-hint" maxLength={32768}
      value={text} onChange={event => onChange(event.target.value)} />
    <p id="expanded-prompt-hint">Enter / Escape / Ctrl+Enter close · Shift+Enter new line · Draft preserved; nothing is sent.</p>
  </dialog>;
}
