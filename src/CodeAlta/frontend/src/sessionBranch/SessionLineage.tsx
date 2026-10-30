import type { ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import type { SessionLineage } from "./lineage";

type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;

/**
 * What the count of a session's origin says: the turns a fork started with, or the turn its source was rewound
 * to, for the session that keeps what the rewind removed (which holds every turn the source had).
 */
export function lineageDetail(t: Translate, lineage: SessionLineage): string {
  if (lineage.kind === "rewind") return lineage.turnCount > 0 ? t("rewound to the end of turn {number}", { number: lineage.turnCount }) : t("rewound to its start");
  return lineage.turnCount === 0 ? t("started empty") : t("started with {turns}", { turns: lineage.turnCount === 1 ? t("1 turn") : t("{count} turns", { count: lineage.turnCount }) });
}

// A sentence of the selected language with a control where it names something: "Forked from {title}" has its
// title elsewhere in other languages.
function withSlot(sentence: string, slot: ReactNode): ReactNode {
  const [before, after] = sentence.split("\u0000");
  return <>{before}{slot}{after}</>;
}

/**
 * Says, above the timeline of a session, that it is a fork of another one, or what a rewind kept of it, and how
 * many turns it started with. The title of the source opens that session while it still exists.
 */
export function SessionLineageBanner({ lineage, title, onOpen }: {
  lineage: SessionLineage;
  /** The title the source has now, or the one it had when it is gone. */
  title: string;
  /** Opens the source; absent when it no longer exists. */
  onOpen?: () => void;
}) {
  const { t } = useShellLanguage();
  const name = onOpen ? <button type="button" className="session-lineage-source" title={t("Open the session {title}", { title })} onClick={onOpen}>{title}</button>
    : <strong title={t("This session no longer exists.")}>{title}</strong>;
  return <p className="session-lineage" data-kind={lineage.kind}>
    <AppIcon name={lineage.kind === "rewind" ? "rewind" : "branch"} size={14} />
    <span>{withSlot(t(lineage.kind === "rewind" ? "Kept from a rewind of {title}" : "Forked from {title}", { title: "\u0000" }), name)}
      {" · "}{lineageDetail(t, lineage)}</span>
  </p>;
}

/** The mark of a fork on the row of a session, with where it comes from. */
export function SessionLineageMark({ lineage }: { lineage: SessionLineage }) {
  const { t } = useShellLanguage();
  const label = t(lineage.kind === "rewind" ? "Kept from a rewind of {title}" : "Forked from {title}", { title: lineage.title });
  return <span className="session-lineage-mark" role="img" aria-label={label} title={label} data-kind={lineage.kind}>
    <AppIcon name={lineage.kind === "rewind" ? "rewind" : "branch"} size={12} /></span>;
}
