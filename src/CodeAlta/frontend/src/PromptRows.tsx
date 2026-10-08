import { Button, Card } from "@blueprintjs/core";
import type { AgentPromptEntry } from "#neoastra";
import { AppIcon } from "./AppIcon";
import type { MessageKey } from "./localization";
import { PromptTags } from "./PromptTags";
import { scopedKey } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";

/** Stable list identity of a prompt: its kind, where it is kept and its id. */
export const promptIdentity = (entry: Pick<AgentPromptEntry, "kind" | "scope" | "id">) => scopedKey(`${entry.kind}:${entry.scope}`, entry.id);
const identity = promptIdentity;

/**
 * The prompts of one kind in the list of the page. A row opens the form of its prompt, and its button opens the file
 * of the prompt in the code editor: to be edited, or to be read for a prompt that ships with CodeAlta.
 */
export function PromptRows({ prompts, title, selected, disabled = false, onSelect, onOpen }: {
  prompts: readonly AgentPromptEntry[]; title: MessageKey; selected: string | null; disabled?: boolean;
  onSelect: (prompt: AgentPromptEntry) => void;
  /** Opens the file of a prompt in the code editor; without it the rows have no button. */
  onOpen?: (prompt: AgentPromptEntry) => void;
}) {
  const { t } = useShellLanguage();
  if (prompts.length === 0) return null;
  return <>
    <h3 className="settings-editor-group">{t(title)}</h3>
    {prompts.map(prompt => { const id = identity(prompt), name = prompt.name || prompt.id, view = prompt.scope === "BuiltIn";
      return <Card key={id} interactive selected={selected === id} aria-current={selected === id ? "true" : undefined}
        onClick={event => { if (!(event.target as HTMLElement).closest(".bp6-button")) onSelect(prompt); }}>
        <span className="settings-editor-name"><strong>{name}</strong><small>{prompt.description || prompt.id}</small></span>
        <PromptTags prompt={prompt}>
          {onOpen && <Button variant="minimal" size="small" icon={<AppIcon name="code" size={15} />} disabled={disabled}
            aria-label={view ? `${t("Open in the code editor")}: ${name}` : t("Edit {name}", { name })} title={t(view ? "Open in the code editor" : "Edit in the code editor")}
            onClick={() => onOpen(prompt)} />}
        </PromptTags>
      </Card>; })}
  </>;
}
