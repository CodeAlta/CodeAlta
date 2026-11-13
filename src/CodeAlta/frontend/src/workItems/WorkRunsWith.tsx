// What a session started for a task or a plan runs with, as the Work items tab and the card of a session choose it: the
// form of the Agent chip with the tiers of models and Adaptive, as the composer of a new session offers them.
import { useState } from "react";
import { useShellLanguage } from "../shellLanguage";
import { defaultSessionSettings, tieredStartRequest, type SessionSettings, type TieredStartRequest } from "../sessionSettings/sessionSettings";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import { SessionSettingsChip, useSessionSettingsChoices } from "../sessionSettings/SessionSettingsChip";
import type { RunProvider, RunsWith } from "./runsWith";

/** What the session that shows an item runs with, which a new session started from its card takes by default. */
export type ShowingSession = Readonly<{ runsWith: RunsWith | null; tier: string | null }>;

type Choice = Readonly<{ key: string; settings: SessionSettings; tier: string | null }>;

/**
 * The choices of the start of an item, until the user starts it or shows another item (`key`): what it runs with,
 * else the defaults, which are what the item was proposed with (`recorded`).
 */
export function useWorkRunsWith(key: string, api: SessionSettingsApi | null, providers: readonly RunProvider[], recorded: RunsWith | null | undefined) {
  const [choice, setChoice] = useState<Choice>({ key, settings: defaultSessionSettings, tier: null });
  const current = choice.key === key ? choice : { key, settings: defaultSessionSettings, tier: null };
  const choices = useSessionSettingsChoices(api, providers, recorded, current.settings);
  return {
    settings: current.settings, tier: current.tier, choices,
    setSettings: (settings: SessionSettings) => setChoice(previous => ({ ...(previous.key === key ? previous : current), settings })),
    setTier: (tier: string | null) => setChoice(previous => ({ ...(previous.key === key ? previous : current), tier })),
    /** What the start asks of the host. */
    request: (): TieredStartRequest => tieredStartRequest(current.settings, choices.shown, current.tier),
  };
}

export type WorkRunsWithState = ReturnType<typeof useWorkRunsWith>;

/**
 * The Agent chip of the start of an item, with its label: the chip shows what the new session runs with, and opens the
 * whole form, tiers and Adaptive included. `inheritedTier` is the tier the defaults give.
 */
export function WorkRunsWithField({ id, providers, runs, inheritedTier = null, disabled, compact = false }: {
  id: string; providers: readonly RunProvider[]; runs: WorkRunsWithState; inheritedTier?: string | null; disabled: boolean;
  /** In the card of a session: a smaller chip, which opens the same form. */
  compact?: boolean;
}) {
  const { t } = useShellLanguage();
  if (!runs.choices.provider) return null;
  return <div className="work-runs-with" role="group" aria-label={t("Runs with")} data-compact={compact || undefined}>
    <span className="work-runs-with-label">{t("Runs with")}</span>
    <SessionSettingsChip id={id} providers={providers} choices={runs.choices} value={runs.settings} disabled={disabled} onChange={runs.setSettings}
      tier={{ value: runs.tier, inherited: inheritedTier, onChange: runs.setTier }} />
  </div>;
}
