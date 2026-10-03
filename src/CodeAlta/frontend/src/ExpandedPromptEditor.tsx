import { useRef, type ClipboardEventHandler, type ReactNode } from "react";
import { AppWindow } from "./AppWindow";
import { dispatchExpandedComposerKey } from "./composerKeyboard";
import { ProjectReferencePicker } from "./ProjectReferencePicker";
import { useShellLanguage } from "./shellLanguage";
import { PromptEditor, type PromptInput } from "./PromptEditor";

export function ExpandedPromptEditor({ text, onChange, onClose, onPaste, attachments, onCompositionStart }: {
  text: string; onChange: (text: string) => void; onClose: () => void;
  onPaste?: ClipboardEventHandler<HTMLElement>; attachments?: ReactNode;
  onCompositionStart?: () => void;
}) {
  const { t } = useShellLanguage();
  const editor = useRef<PromptInput>(null);

  return <AppWindow storageKey="codealta.desktop.window.prompt-editor.v1" className="expanded-prompt-dialog" titleId="expanded-prompt-title" title={t("Edit prompt")}
    preferredSize={viewport => ({ width: Math.min(1000, viewport.width - 40), height: Math.min(760, viewport.height - 40) })}
    minimumSize={{ width: 420, height: 280 }} onClose={onClose} closeLabel={t("Close")} onOpened={() => editor.current?.focus()}
    onCancel={event => { event.preventDefault(); onClose(); }} onKeyDown={event => {
      // Modal editing must never fall through to shell shortcuts or the regular composer's Send/Steer.
      event.stopPropagation();
      // Metadata/picker buttons retain native keyboard activation. Only Monaco
      // editing keys (and dialog-wide Escape) use the editor close shortcut.
      if (!editor.current?.contains(event.target as Node) && event.key !== "Escape") return;
      if (dispatchExpandedComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
        altKey: event.altKey, metaKey: event.metaKey, repeat: event.repeat,
        isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode,
        defaultPrevented: event.defaultPrevented }, onClose)) event.preventDefault();
      else if (event.key === "Escape") event.preventDefault();
    }}>
    {attachments}
    <div className="expanded-prompt-panes">
      <PromptEditor ref={editor} expanded label={t("Expanded prompt")}
        value={text} onChange={onChange} onPaste={onPaste} onCompositionStart={onCompositionStart}
        onKeyDown={event => {
          if (dispatchExpandedComposerKey({ ...event, isComposing: event.nativeEvent.isComposing,
            keyCode: event.nativeEvent.keyCode }, onClose)) { event.preventDefault(); event.stopPropagation(); }
        }} />
    </div>
    <ProjectReferencePicker text={text} edit={onChange} input={editor} />
    <p id="expanded-prompt-hint">{t("Escape / Ctrl+Enter close · Enter new line · Draft preserved; nothing is sent.")}</p>
  </AppWindow>;
}
