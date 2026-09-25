// Instance-owned revision for read-only recovery views. Never starts a read or a retry.
export function createOwnerChangeSignal() {
  let revision = 0;
  const listeners = new Set<() => void>();
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => revision,
    changed() { revision++; for (const listener of listeners) { try { listener(); } catch { /* Presentation cannot interrupt owner transport or retention. */ } } },
  };
}
