// What every section of the page of a space is given. Each section is a component of its own, in a file of its
// own, so that the work on one part of a space (what is available there, its new sessions, its identity) changes
// that file only.
import type { Space } from "./spaces";
import type { SpaceOutcome, SpacesHub } from "./spacesHub";

/** The space a section shows, the others, and whether the host lets it be changed. */
export type SpaceSectionProps = Readonly<{
  space: Space;
  spaces: readonly Space[];
  hub: SpacesHub;
  /** Whether the host lets the space be changed. */
  canEdit: boolean;
  /** Whether a change of the page is running: the controls wait for it. */
  busy: boolean;
  /** Runs a change of the page: its notice says how it ended. */
  run: (change: () => Promise<SpaceOutcome>, said?: boolean) => Promise<SpaceOutcome>;
}>;
