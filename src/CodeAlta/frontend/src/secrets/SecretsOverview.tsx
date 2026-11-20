import { Button } from "@blueprintjs/core";
import type { SecretsList } from "#neoastra";
import { AppIcon, type IconName } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { stateLabel } from "./credentials";
import { missingSecrets, projectIssues, projectsWithEnvironment, secretsNotReady, secretsTabTitles, type EnvironmentList, type SecretsTab } from "./secretsTabs";

/** A card of the overview: a concept of the page, what it is in one sentence, how many there are, what needs the user, and its first action. */
type OverviewCard = Readonly<{
  tab: SecretsTab; icon: IconName; sentence: MessageKey; count: number; attention: readonly string[];
  action: MessageKey; onAction: () => void; disabled?: boolean;
}>;

/**
 * The Overview tab: a card per concept of the page (secrets, variables, environment, vaults, sign-ins). A click on a card
 * opens its tab; its button starts its first action there.
 */
export function SecretsOverview({ listing, environment, offered, busy, onOpen, onAddSecret, onAddVariable, onSetEnvironment, onAddVault, onAddSignIn }: {
  listing: SecretsList; environment: EnvironmentList | null;
  /** The tabs the page offers: a concept without a tab has no card. */
  offered: readonly SecretsTab[]; busy: boolean; onOpen: (tab: SecretsTab) => void;
  onAddSecret: () => void; onAddVariable: () => void; onSetEnvironment: () => void; onAddVault: () => void; onAddSignIn: () => void;
}) {
  const { t } = useShellLanguage();
  const locked = listing.secrets.filter(secret => secret.state === "locked").length;
  const other = secretsNotReady(listing).length - locked;
  const missing = missingSecrets(environment);
  const withIssues = projectsWithEnvironment(environment).filter(project => projectIssues(project).length > 0).length;
  const cards: OverviewCard[] = [
    { tab: "secrets", icon: "secret", sentence: "Values the configuration names as ${secret:NAME}; never shown again.", count: listing.secrets.length,
      attention: [...locked > 0 ? [t("{count} locked", { count: locked })] : [], ...other > 0 ? [t("{count} not ready", { count: other })] : [],
        ...missing.length === 1 ? [t("{name} is not set", { name: missing[0] })] : missing.length > 1 ? [t("{count} secrets not set", { count: missing.length })] : []],
      action: "Add a secret…", onAction: onAddSecret },
    { tab: "variables", icon: "tag", sentence: "Values that are not secret, named as ${var:NAME}.", count: listing.variables.length, attention: [],
      action: "Add a variable…", onAction: onAddVariable },
    { tab: "environment", icon: "terminal", sentence: "Environment variables the sessions of a project get.", count: projectsWithEnvironment(environment).length,
      attention: withIssues === 1 ? [t("1 project misses a value")] : withIssues > 1 ? [t("{count} projects miss a value", { count: withIssues })] : [],
      action: "Add a project…", onAction: onSetEnvironment },
    { tab: "vaults", icon: "lock", sentence: "Where the values are kept: this computer, or a vault such as 1Password.", count: listing.providers.length,
      attention: listing.providers.filter(provider => provider.state !== "ready").map(provider => `${provider.displayName}: ${t(stateLabel(provider.state).label)}`),
      action: "Add a vault…", onAction: onAddVault },
    { tab: "signIns", icon: "globe", sentence: "Sign-ins the browser of a session types on their websites.", count: listing.signIns.length, attention: [],
      action: "Add a sign-in…", onAction: onAddSignIn, disabled: listing.providers.length === 0 },
  ];
  return <ul className="secrets-overview" aria-label={t("Overview")}>
    {cards.filter(card => offered.includes(card.tab)).map(card => <li key={card.tab} className="secrets-overview-card" data-overview={card.tab} data-attention={card.attention.length > 0 || undefined}>
      <button type="button" className="secrets-overview-open settings-own-button" onClick={() => onOpen(card.tab)}>
        <span className="secrets-overview-head">
          <span className="secrets-overview-icon"><AppIcon name={card.icon} size={18} /></span>
          <strong>{t(secretsTabTitles[card.tab])}</strong>
          <span className="secrets-overview-count">{card.count}</span>
        </span>
        <span className="secrets-overview-sentence">{t(card.sentence)}</span>
        {card.attention.length > 0 && <span className="secrets-overview-attention"><AppIcon name="warning" size={13} />{card.attention.join(" · ")}</span>}
      </button>
      <Button size="small" variant="minimal" intent="primary" className="secrets-overview-action" icon={<AppIcon name="plus" size={14} />} text={t(card.action)}
        disabled={busy || card.disabled} onClick={card.onAction} />
    </li>)}
  </ul>;
}
