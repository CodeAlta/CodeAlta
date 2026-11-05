/** What the Notes overlay of a session remembers while its pane is not mounted. */
export type NotesOverlayMemory = {
  /** The user collapsed Notes, or they opened collapsed and stayed so. */
  collapsed: boolean;
  /** The last notes shown had content: loading the same notes again does not expand them. */
  hadContent: boolean;
};

/** What the proposals overlay of a session remembers while its pane is not mounted. */
export type ProposalsOverlayMemory = {
  /** The user put the proposals away. */
  collapsed: boolean;
  /** The keys of every item shown so far; null before the first. A key not among them is a new proposal. */
  known: ReadonlySet<string> | null;
};

/**
 * The state of the overlays of the session panes, by session, for the lifetime of a window. A pane is
 * mounted again when the user switches spaces: without it, Notes and proposals the user had collapsed
 * would open again. The objects are owned by their pane while it is mounted, which writes them.
 */
export function createSessionOverlayMemory() {
  const notes = new Map<string, NotesOverlayMemory>();
  const proposals = new Map<string, ProposalsOverlayMemory>();
  return {
    /** The memory of the Notes overlay of a session: the same object for every call. */
    notes(sessionId: string): NotesOverlayMemory {
      let value = notes.get(sessionId);
      if (!value) notes.set(sessionId, value = { collapsed: true, hadContent: false });
      return value;
    },
    /** The memory of the proposals overlay of a session: the same object for every call. */
    proposals(sessionId: string): ProposalsOverlayMemory {
      let value = proposals.get(sessionId);
      if (!value) proposals.set(sessionId, value = { collapsed: false, known: null });
      return value;
    },
  };
}
