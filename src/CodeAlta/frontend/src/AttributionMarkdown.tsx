import { Button } from "@blueprintjs/core";
import { CodeEditor } from "./monaco/CodeEditor";
import { useAttributionDraft, type ConfirmTextRemoval } from "./attributionText";
import { useShellLanguage } from "./shellLanguage";

/**
 * An attribution text written in Markdown, such as the instruction about what agents write elsewhere: a Markdown editor
 * as wide as its card, which starts with the text agents get, the one of CodeAlta until the user writes another. Save,
 * or Ctrl+S, keeps it on one line; Use CodeAlta's text goes back to the one of CodeAlta.
 */
export function AttributionMarkdown({ label, value, defaultValue, maxLength, disabled, onSave, confirm }: {
  label: string; value: string; defaultValue: string; maxLength: number; disabled: boolean; onSave: (text: string) => void;
  /** Asks before a text that no longer names CodeAlta is saved. */
  confirm?: ConfirmTextRemoval;
}) {
  const { t } = useShellLanguage();
  const text = useAttributionDraft(value, defaultValue, onSave, confirm);
  const tooLong = text.draft.replace(/\s+/g, " ").trim().length > maxLength;
  return <div className="settings-field-wide attribution-markdown">
    <div className="attribution-markdown-editor">
      <CodeEditor language="markdown" wrap label={label} value={text.draft} readOnly={disabled} onChange={text.setDraft} onSave={() => { if (!tooLong) text.save(); }} />
    </div>
    <div className="attribution-markdown-actions">
      {tooLong && <span className="attribution-markdown-limit">{t("At most {count} characters.", { count: String(maxLength) })}</span>}
      <Button size="small" variant="minimal" icon="reset" text={t("Use CodeAlta's text")} disabled={disabled || (!text.own && text.draft === defaultValue)}
        onClick={text.restore} />
      <Button size="small" intent="primary" text={t("Save")} disabled={disabled || !text.pending || tooLong} onClick={text.save} />
    </div>
  </div>;
}
