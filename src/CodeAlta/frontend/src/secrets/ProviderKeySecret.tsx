import { useState } from "react";
import { Button, InputGroup, PopoverNext, Tag } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { isSecretName, suggestedSecretName } from "./secrets";

/** What the key part of a provider form needs of the provider as the configuration has it. */
export type ProviderKeySource = Readonly<{ key: string; hasApiKey: boolean; apiKeySecret?: string | null; apiKeySecretSet?: boolean | null; keyInEnvironment?: boolean | null }>;

/**
 * Under the key of a provider: the secret of this machine that holds it, or, for a key in the configuration file or in
 * its environment variable, Keep as a secret, which moves it into a secret the configuration then names.
 */
export function ProviderKeySecret({ provider, disabled, onKeep }: { provider: ProviderKeySource; disabled: boolean; onKeep: (name: string) => void }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(() => suggestedSecretName(provider.key));
  if (provider.apiKeySecret) {
    return <div className="provider-key-secret" data-secret={provider.apiKeySecret}>
      <AppIcon name="secret" size={14} />
      <span>{t("The key is the secret {name}.", { name: provider.apiKeySecret })}</span>
      {!provider.apiKeySecretSet && <Tag minimal intent="warning">{t("The secret {name} is not set on this machine.", { name: provider.apiKeySecret })}</Tag>}
    </div>;
  }

  if (!provider.hasApiKey && !provider.keyInEnvironment) return null;
  const valid = isSecretName(name.trim());
  return <div className="provider-key-secret">
    <PopoverNext isOpen={open} placement="bottom-start" onInteraction={next => { if (!next) setOpen(false); }}
      content={<form className="provider-key-secret-form" onSubmit={event => { event.preventDefault(); if (valid) { setOpen(false); onKeep(name.trim()); } }}>
        <strong>{t("Name of the secret")}</strong>
        <InputGroup small autoFocus value={name} intent={valid ? "none" : "danger"} aria-label={t("Name of the secret")} onChange={event => setName(event.target.value)} />
        <p>{t("The key moves to a secret of this machine; the configuration file names it.")}</p>
        <div className="rename-actions">
          <Button variant="minimal" size="small" text={t("Cancel")} onClick={() => setOpen(false)} />
          <Button type="submit" intent="primary" size="small" text={t("Keep")} disabled={!valid} />
        </div>
      </form>}>
      <Button size="small" icon={<AppIcon name="secret" size={14} />} text={t("Keep as a secret…")} disabled={disabled} onClick={() => setOpen(true)} />
    </PopoverNext>
  </div>;
}
