import { useCallback, useEffect, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { secrets, type SecretsList } from "#neoastra";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { stateLabel } from "./credentials";
import { VaultBindingDialog } from "./VaultPicker";
import { suggestedSecretName } from "./secrets";
import type { ProviderKeySource } from "./ProviderKeySecret";

type VaultApi = Pick<typeof secrets, "list" | "browse" | "bindSecret">;

/**
 * Under the key of a provider, when the machine has vaults: where its secret is kept and whether it can be given now, and Use
 * a vault item…, which binds a secret to an item of a vault and gives the provider its key from it.
 */
export function ProviderKeyVault({ epoch, provider, disabled, onUse, api = secrets }: {
  epoch: string; provider: ProviderKeySource; disabled: boolean; onUse: (name: string) => Promise<unknown>; api?: VaultApi;
}) {
  const { t } = useShellLanguage();
  const [listing, setListing] = useState<SecretsList | null>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    api.list({ expectedEpoch: epoch }).then(value => { if (alive && value.status === "ok") setListing(value); }).catch(() => { });
    return () => { alive = false; };
  }, [api, epoch, provider.apiKeySecret]);
  const browse = useCallback((id: string, parent: string | null, signal?: AbortSignal) => api.browse({ expectedEpoch: epoch, id, parent }, { signal }), [api, epoch]);
  if (!listing || listing.providers.length === 0) return null;
  const secret = provider.apiKeySecret ? listing.secrets.find(item => item.name.toLowerCase() === provider.apiKeySecret!.toLowerCase()) : null;
  const state = secret ? stateLabel(secret.state) : null;
  return <div className="provider-key-secret provider-key-vault">
    {secret?.provider && <span className="secret-source"><CredentialIcon icon={secret.icon} size={14} />
      <span>{t("Kept in {vault}", { vault: `${secret.providerName ?? secret.provider}${secret.label ? ` › ${secret.label}` : ""}` })}</span></span>}
    {secret && state && secret.state !== "ready" && <Tag minimal intent={state.intent} title={secret.stateMessage ?? undefined}>{t(state.label)}</Tag>}
    {!secret?.provider && <Button size="small" icon={<CredentialIcon icon={listing.providers[0].icon} size={14} />} text={t("Use a vault item…")} disabled={disabled || busy}
      onClick={() => setOpen(true)} />}
    {problem && <span className="secret-problem" role="alert">{problem}</span>}
    {open && <VaultBindingDialog providers={listing.providers} types={listing.types} names={listing.secrets.map(item => item.name)}
      localNames={listing.secrets.filter(item => !item.provider).map(item => item.name)} initialName={provider.apiKeySecret ?? suggestedSecretName(provider.key)} nameEditable={!provider.apiKeySecret}
      busy={busy} browse={browse} onClose={() => setOpen(false)}
      onBind={async value => {
        setBusy(true); setProblem(null);
        try {
          const result = await api.bindSecret({ expectedEpoch: epoch, name: value.name, provider: value.provider, reference: value.reference, label: value.label, replaceLocal: value.replaceLocal });
          if (result.status !== "ok") { setProblem(result.message ?? t("The secret could not be kept in the vault.")); return false; }
          await onUse(value.name);
          return true;
        } finally {
          setBusy(false);
        }
      }} />}
  </div>;
}
