import { useEffect, useRef, type ClipboardEventHandler, type ReactNode } from "react";
import { dispatchExpandedComposerKey } from "./composerKeyboard";
import { ProjectReferencePicker } from "./ProjectReferencePicker";
import { useShellLanguage } from "./shellLanguage";

export function ExpandedPromptEditor({ text, onChange, onClose, onPaste, attachments }: {
  text: string; onChange: (text: string) => void; onClose: () => void;
  onPaste?: ClipboardEventHandler<HTMLTextAreaElement>; attachments?: ReactNode;
}) {
  const { t } = useShellLanguage();
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
      // Metadata/picker buttons retain native keyboard activation. Only textarea
      // editing keys (and dialog-wide Escape) use the editor close shortcut.
      if (event.target !== editor.current && event.key !== "Escape") return;
      if (dispatchExpandedComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
        altKey: event.altKey, metaKey: event.metaKey, repeat: event.repeat,
        isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode,
        defaultPrevented: event.defaultPrevented }, onClose)) event.preventDefault();
      else if (event.key === "Escape") event.preventDefault();
    }}>
    <header><h2 id="expanded-prompt-title">{t("Edit prompt")}</h2><button type="button" onClick={onClose}>{t("Close")}</button></header>
    <textarea ref={editor} aria-label={t("Expanded prompt")} aria-describedby="expanded-prompt-hint" maxLength={32768}
      value={text} onChange={event => onChange(event.target.value)} onPaste={onPaste} />
    {attachments}
    <ProjectReferencePicker text={text} edit={onChange} input={editor} />
    <p id="expanded-prompt-hint">{t("Enter / Escape / Ctrl+Enter close · Shift+Enter new line · Draft preserved; nothing is sent.")}</p>
  </dialog>;
}
