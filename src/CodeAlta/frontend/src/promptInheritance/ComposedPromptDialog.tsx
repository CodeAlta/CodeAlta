// The text an agent receives from a prompt that inherits others: the inherited prompts in order, then the prompt, as
// one read-only Markdown text, with the changes of the editor not saved yet.
import { Dialog, DialogBody } from "@blueprintjs/core";
import { CodeEditor } from "../monaco/CodeEditor";
import { scopeLabel } from "../PromptTags";
import { useShellLanguage } from "../shellLanguage";
import { tokenCount } from "./inheritance";
import type { PromptComposition } from "./PromptInheritance";

/** The dialog that shows the composed text of an agent prompt. */
export function ComposedPromptDialog({ isOpen, name, composition, onClose }: {
  isOpen: boolean; name: string; composition: PromptComposition; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const ownTokens = Math.max(0, composition.tokens - composition.parts.reduce((sum, part) => sum + part.tokens, 0));
  const title = t("Composed prompt: {name}", { name });
  return <Dialog isOpen={isOpen} onClose={onClose} className="prompt-inheritance-preview" title={title}>
    <DialogBody>
      <p className="prompt-inheritance-note">{t("The text the agent receives, with the changes not saved yet.")}</p>
      <ol className="prompt-inheritance-parts">
        {composition.parts.map(part => <li key={part.id}><strong>{part.name}</strong> <code>{part.id}</code>{" "}
          <small>{t(scopeLabel(part.scope))} · {t("about {tokens} tokens", { tokens: tokenCount(part.tokens) })}</small></li>)}
        <li><strong>{t("This prompt")}</strong> <small>{t("about {tokens} tokens", { tokens: tokenCount(ownTokens) })}</small></li>
      </ol>
      {composition.truncated && <p className="prompt-inheritance-note" role="status">{t("The composed text is longer than what is shown.")}</p>}
      <div className="prompt-inheritance-text"><CodeEditor language="markdown" wrap readOnly label={title} value={composition.body} onChange={() => undefined} /></div>
    </DialogBody>
  </Dialog>;
}
