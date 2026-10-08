import { useShellLanguage } from "../shellLanguage";
import { changeBar } from "./projectChanges";

/** The lines a change added and removed, as `+12 −3`; nothing when neither is known. */
export function Counts({ insertions, deletions }: { insertions: number | null; deletions: number | null }) {
  const { locale } = useShellLanguage();
  if (insertions === null && deletions === null) return null;
  return <span className="changes-counts"><b>+{(insertions ?? 0).toLocaleString(locale)}</b><em>−{(deletions ?? 0).toLocaleString(locale)}</em></span>;
}

/** Five squares that show how much of a change is additions and how much is removals. */
export function ChangeBar({ insertions, deletions }: { insertions: number | null; deletions: number | null }) {
  const bar = changeBar(insertions, deletions);
  return <span className="changes-bar" aria-hidden="true">{[0, 1, 2, 3, 4].map(index =>
    <i key={index} data-change={index < bar.added ? "added" : index < bar.added + bar.removed ? "removed" : undefined} />)}</span>;
}
