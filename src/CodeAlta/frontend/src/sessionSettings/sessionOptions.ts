// The options of a session beside its agent prompt, model, permission mode and sandbox: what its agent may do, such as
// starting sessions of its own, the size of its context and its fast mode. Every option has a default; null stands for
// the defaults.
import type { SessionOptionsChoice } from "#neoastra";

/** The options of a session that chose none: every option on. */
export const defaultSessionOptions: SessionOptionsChoice = Object.freeze({ subSessions: true });

/** A model as the size of its context is chosen: the sizes it has, and the one of a session that chose none. */
export type ModelContextSizes = Readonly<{ contextSizes?: readonly number[] | null; contextSize?: number | null }>;

/**
 * A model as the options of a session are chosen for it: its sizes of context, and its fast mode, null for a model without
 * one, else whether it runs fast for a session that chose nothing.
 */
export type SessionOptionsModel = ModelContextSizes & Readonly<{ fastMode?: boolean | null }>;

/** The size of context a session uses where its model has several: the one it chose, else the one of a session that chose none. */
export function sessionContextSize(model: ModelContextSizes | null | undefined, options: SessionOptionsChoice | null | undefined): number | null {
  const chosen = options?.contextSize;
  return !model?.contextSizes?.length ? null : chosen && model.contextSizes.includes(chosen) ? chosen : model.contextSize ?? null;
}

/**
 * Whether a session runs its model in fast mode, as its switch and the lightning of its chip show it: the choice of the
 * session, else what the provider last reported for it, else the default of the model; null for a model without one.
 */
export function sessionFastMode(model: SessionOptionsModel | null | undefined, options: SessionOptionsChoice | null | undefined,
  reported: boolean | null = null): boolean | null {
  return model?.fastMode == null ? null : options?.fastMode ?? reported ?? model.fastMode;
}

/** The options a value stands for: null is the defaults. */
export function sessionOptionsOf(options: SessionOptionsChoice | null | undefined): SessionOptionsChoice {
  return options ?? defaultSessionOptions;
}

/**
 * How many options differ from their default: the form shows them without being asked, the chip marks them. A size of
 * context counts only where the model of the session has it: another one is kept, and not used; without a model, none.
 * A fast mode counts where it differs from the one of the model.
 */
export function changedSessionOptions(options: SessionOptionsChoice | null | undefined, model?: SessionOptionsModel | null): number {
  const value = sessionOptionsOf(options);
  return (value.subSessions ? 0 : 1) + (value.contextSize && model?.contextSizes?.includes(value.contextSize) ? 1 : 0) + (value.private ? 1 : 0)
    + (value.fastMode != null && model?.fastMode != null && value.fastMode !== model.fastMode ? 1 : 0);
}

/**
 * What a choice records: null for the defaults, so that a session without a choice records nothing. A fast mode is
 * recorded once chosen, on or off: a session that chose none follows its provider.
 */
export function storedSessionOptions(options: SessionOptionsChoice | null | undefined): SessionOptionsChoice | null {
  return options && (!options.subSessions || options.contextSize || options.private || options.fastMode != null)
    ? { subSessions: options.subSessions, ...(options.contextSize ? { contextSize: options.contextSize } : {}), ...(options.private ? { private: true } : {}),
      ...(options.fastMode != null ? { fastMode: options.fastMode } : {}) } : null;
}
