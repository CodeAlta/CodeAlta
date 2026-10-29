import { memo } from "react";
import { MarkdownContent } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptPresented } from "../timeline";
import { useWholeMessage, WholeMessageNote } from "../WholeMessageNote";

/**
 * What a script showed the user (`present()`), in their order: each part as Markdown, as it came. A part the history page
 * gave the start of says so, and reads the rest from the journal when asked.
 */
export const PresentedParts = memo(function PresentedParts({ parts, onOpenLink }: {
  parts: ReadonlyArray<ScriptPresented>;
  /** Opens a link of a part; null where nothing opens one. */
  onOpenLink: ((address: string) => void) | null;
}) {
  const { t } = useShellLanguage();
  return <div className="script-presented" role="group" aria-label={t("Presented by the script")}>
    {parts.map(part => <PresentedPart key={part.key} part={part} onOpenLink={onOpenLink} />)}
  </div>;
});

function PresentedPart({ part, onOpenLink }: { part: ScriptPresented; onOpenLink: ((address: string) => void) | null }) {
  const whole = useWholeMessage(part.shortened ? part.offset : null);
  const source = whole.state.status === "ok" ? whole.state.text : part.markdown;
  return <section className="script-presented-part" data-step={part.stepId ?? undefined}>
    <MarkdownContent source={source} timelineCodeBlocks onOpenLink={onOpenLink} />
    {part.shortened && <WholeMessageNote state={whole.state} load={whole.load} />}
  </section>;
}
