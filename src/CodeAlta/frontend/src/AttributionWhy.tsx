import { useState, type ReactNode } from "react";
import { Button, Dialog, DialogBody, DialogFooter, Icon } from "@blueprintjs/core";
import { useShellLanguage } from "./shellLanguage";

/**
 * Why agents say that CodeAlta took part in their work, above the two cards that choose it: the provenance of what they
 * write, and the visibility a free, open-source project lives on. It thanks the user rather than insists.
 */
export function AttributionWhy() {
  const { t } = useShellLanguage();
  return <section className="attribution-why" aria-labelledby="attribution-why-title">
    <h2 id="attribution-why-title"><Icon icon="heart" className="attribution-why-heart" />{t("Why CodeAlta signs its work")}</h2>
    <div className="attribution-why-reasons">
      <div className="attribution-why-reason">
        <Icon icon="history" size={18} />
        <div><strong>{t("Clear provenance")}</strong>
          <p>{t("Your name is already on the commit or the message. A short line of CodeAlta tells your readers that an AI coding agent did the work, so that they review it knowing how it was made.")}</p></div>
      </div>
      <div className="attribution-why-reason">
        <Icon icon="globe-network" size={18} />
        <div><strong>{t("Help CodeAlta grow")}</strong>
          <p>{t("CodeAlta is free and open source. Each line brings it a little visibility where it is used: that is how others find it, and how the project thrives.")}</p></div>
      </div>
    </div>
    <p className="attribution-why-thanks">{t("Both are on by default. Thank you for keeping them!")}</p>
  </section>;
}

/** What a confirmation asks about: a switch turned off, or a text that leaves CodeAlta out. */
export type AttributionRemoval = "co-author" | "external-writing" | "text";

/**
 * Asks once, kindly, before a change takes CodeAlta out of what agents write: turning off one of the two lines, or
 * saving a text that no longer names CodeAlta. Keeping it is the default answer: closing the dialog in any way keeps it.
 * Nothing else asks: turning a line on, or a text that still names CodeAlta, goes through at once.
 */
export function useAttributionConfirmation(): { confirm: (removal: AttributionRemoval, proceed: () => void, keep?: () => void) => void; dialog: ReactNode } {
  const { t } = useShellLanguage();
  const [asking, setAsking] = useState<{ removal: AttributionRemoval; proceed: () => void; keep?: () => void } | null>(null);
  const titles: Record<AttributionRemoval, string> = {
    "co-author": t("Turn off the co-author line?"),
    "external-writing": t("Turn off the line of CodeAlta?"),
    text: t("Leave CodeAlta out of this text?"),
  };
  const keep = () => { const kept = asking?.keep; setAsking(null); kept?.(); };
  const dialog = asking && <Dialog isOpen className="attribution-confirmation" title={titles[asking.removal]} icon="heart" onClose={keep}>
    <DialogBody>
      <p>{t(asking.removal === "text"
        ? "Your text no longer names CodeAlta. Its readers would not know that an AI coding agent did the work, and CodeAlta would lose the visibility it gets from it."
        : "This line tells who did the work, and it helps others discover CodeAlta, which is free and open source.")}</p>
      <p className="attribution-confirmation-ask">{t("Would you keep it? It means a lot to the project.")}</p>
    </DialogBody>
    <DialogFooter actions={<>
      <Button variant="minimal" text={t(asking.removal === "text" ? "Save it anyway" : "Turn it off")}
        onClick={() => { const { proceed } = asking; setAsking(null); proceed(); }} />
      <Button intent="primary" autoFocus icon="heart" text={t("Keep it")} onClick={keep} />
    </>} />
  </Dialog>;
  return { confirm: (removal, proceed, keep) => setAsking({ removal, proceed, keep }), dialog };
}
