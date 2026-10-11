/** A definite result releases a plugin handoff; null/throw means the downstream outcome is unknown. */
export type PluginPromptOutcome = boolean | null;

/** Wait for the command receipt, not merely synchronous event dispatch; a lost waiter is unknown. */
export async function awaitComposerAdmission(dispatch: () => { result: boolean; completion?: Promise<PluginPromptOutcome> }): Promise<PluginPromptOutcome> {
  try { const command = dispatch(); return await (command.completion ?? command.result); }
  catch { return null; }
}

/** Owns prompt deliveries independently of the stream reader, so a close can abort a held admission. */
export function createPluginPromptAdmissions<T>(dispatch: (request: T, signal: AbortSignal) => PluginPromptOutcome | Promise<PluginPromptOutcome>,
  answer: (id: string, accepted: boolean) => void) {
  const requests = new Map<string, { abort: AbortController; outcome: PluginPromptOutcome }>();
  const closed = new Set<string>();
  let disposed = false;
  return {
    receive(id: string, request: T) {
      if (disposed) return;
      const previous = requests.get(id);
      if (previous) { if (previous.outcome !== null) answer(id, previous.outcome); return; }
      // An unknown Close may belong to an earlier page's still-running original worker. Suppress a late
      // delivery here, but never assert that another page's work was conclusively canceled.
      if (closed.has(id)) return;
      const entry = { abort: new AbortController(), outcome: null as PluginPromptOutcome };
      requests.set(id, entry);
      void (async () => {
        try {
          // No effect-scheduled work: dispatch captures its command synchronously and must fence its signal.
          const outcome = await dispatch(request, entry.abort.signal);
          entry.outcome = outcome;
          // A known host acknowledgement remains true even if Close arrived while its receipt was in flight.
          // Abort/transport loss is not a false acknowledgement; uncertainty intentionally sends no response.
          if (outcome !== null && !disposed) answer(id, outcome);
        } catch { /* Unknown whether a command was dispatched: retain the host claim. */ }
      })();
    },
    close(id: string) { closed.add(id); requests.get(id)?.abort.abort(); },
    disconnect() { for (const entry of requests.values()) entry.abort.abort(); },
    dispose() { disposed = true; for (const entry of requests.values()) entry.abort.abort(); },
  };
}
