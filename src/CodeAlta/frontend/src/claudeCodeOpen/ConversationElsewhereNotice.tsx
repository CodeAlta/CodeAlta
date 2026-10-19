import { Button } from "@blueprintjs/core";
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { elsewhereCovers, elsewhereDismiss, elsewhereNotice, sameHolder, type ElsewhereHolder } from "./claudeCodeOpen";

/** How often the holder is read again while its session is shown. */
export const elsewhereNoticeInterval = 5_000;

/** Another Claude process that holds the conversation of a session or a preview, and whether the user dismissed the banner over the prompt for it. */
export type ConversationElsewhere = Readonly<{ held: ElsewhereHolder | null; covers: boolean; dismiss: () => void }>;

/**
 * Reads which other Claude process holds the conversation: when it is shown, then every few seconds while it stays
 * shown and the window is visible. Nothing is held while `running` (the run of the session, or the question of a held
 * conversation, says more), nor while it is not shown or cannot be read: what was read before is forgotten, so that it
 * never covers the prompt before it is read again. A banner collapsed before the first read answered covers what that
 * read finds: it does not come back over the prompt the user went to.
 */
export function useConversationElsewhere(read: (() => Promise<ElsewhereHolder | null>) | null, shown: boolean, running: boolean): ConversationElsewhere {
  const [read_, setRead] = useState<ElsewhereHolder | null>(null);
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => new Set());
  const [dismissedUnread, setDismissedUnread] = useState(false);
  useEffect(() => {
    if (!shown || !read) { setRead(null); return; }
    let alive = true;
    let reading = false;
    const look = () => {
      if (reading || document.hidden) return;
      reading = true;
      // The same holder as before keeps the value read before: nothing is drawn again for it.
      void read().then(value => { if (alive) setRead(current => sameHolder(current, value) ? current : value); }, () => undefined)
        .finally(() => { reading = false; });
    };
    look();
    const timer = window.setInterval(look, elsewhereNoticeInterval);
    document.addEventListener("visibilitychange", look);
    return () => { alive = false; window.clearInterval(timer); document.removeEventListener("visibilitychange", look); setRead(null); };
  }, [read, shown]);
  useEffect(() => {
    if (!dismissedUnread || !read_) return;
    setDismissed(current => elsewhereDismiss(read_, current));
    setDismissedUnread(false);
  }, [dismissedUnread, read_]);
  const held = running ? null : read_;
  return { held, covers: !(dismissedUnread && held) && elsewhereCovers(held, dismissed),
    dismiss: () => { if (held) setDismissed(current => elsewhereDismiss(held, current)); else setDismissedUnread(true); } };
}

/**
 * What takes the keyboard when the prompt is to have it: the prompt, or the **Write a message** button of the banner
 * shown in its place.
 */
export function promptFocusTarget(prompt: HTMLElement | null): HTMLElement | null {
  if (!prompt?.classList.contains("covered")) return prompt;
  return prompt.parentElement?.querySelector<HTMLElement>(".conversation-banner [data-write-message]") ?? prompt;
}

/** What is said of the process that holds the conversation, in the words of the user; null when none holds it. */
export function useElsewhereText(held: ElsewhereHolder | null) {
  const { t } = useShellLanguage();
  const notice = elsewhereNotice(held);
  return notice && { working: notice.working, icon: notice.working ? "eye" as const : "openExternal" as const,
    text: t(notice.message, { where: notice.where ? t(notice.where) : notice.whereText }),
    detail: t("Your next message may ask whether to wait until it is free.") };
}

/**
 * The banner shown in the place of the text of the prompt (the draft is kept, hidden), so that it is read before a
 * message is written: a click on its text or **Write a message** collapses it, and the prompt takes the keyboard.
 */
export function ConversationBanner({ text, children, actions, onCollapse, working = false, primaryWrite }: {
  /** What it says; a click on it collapses the banner. */
  text: ReactNode;
  /** A row between the text and the buttons (the project of a preview). */
  children?: ReactNode;
  /** Buttons before **Write a message**, or after it when it is the main action. */
  actions?: ReactNode;
  onCollapse: () => void;
  /** Another process works on the conversation now: the banner is stronger. */
  working?: boolean;
  /** What **Write a message** says when it is the main action of the banner: it is then primary and first. */
  primaryWrite?: string;
}) {
  const { t } = useShellLanguage();
  const root = useRef<HTMLDivElement>(null);
  // Shown while the prompt had the keyboard (the user went to it, or was typing): the keyboard goes to Write a message
  // rather than to nothing, as the prompt under the banner cannot keep it.
  useLayoutEffect(() => {
    const prompt = root.current?.closest(".owned-session")?.querySelector(".prompt-editor");
    if (prompt?.contains(document.activeElement)) root.current?.querySelector<HTMLElement>("[data-write-message]")?.focus();
  }, []);
  const collapse = () => {
    const composer = root.current?.closest(".owned-session");
    onCollapse();
    requestAnimationFrame(() => composer?.querySelector<HTMLElement>(".prompt-editor")?.focus());
  };
  const write = primaryWrite ?? t("Write a message");
  const writeButton = <Button data-write-message intent={primaryWrite ? "primary" : undefined} icon={<AppIcon name="edit" size={14} />} text={write} onClick={collapse} />;
  return <div ref={root} className={`conversation-banner${working ? " working" : ""}`} role="region" aria-label={t("Before you write")}>
    <button type="button" className="conversation-banner-text" onClick={collapse} title={write}>{text}</button>
    {children && <div className="conversation-banner-row">{children}</div>}
    <div className="conversation-banner-actions">
      {primaryWrite ? <>{writeButton}{actions}</> : <>{actions}{writeButton}</>}
    </div>
  </div>;
}

/** The banner of a session whose conversation another Claude process holds. */
export function ConversationElsewhereBanner({ elsewhere }: { elsewhere: ConversationElsewhere }) {
  const said = useElsewhereText(elsewhere.held);
  if (!said || !elsewhere.covers) return null;
  return <ConversationBanner working={said.working} onCollapse={elsewhere.dismiss}
    text={<><span className="conversation-banner-title"><AppIcon name={said.icon} size={15} />{said.text}</span><span>{said.detail}</span></>} />;
}

/**
 * Right above the prompt, once its banner is collapsed: one line with what the banner said that still matters while a
 * message is written (`leading`: the project of a preview), and the process that still holds the conversation.
 */
export function ConversationElsewhereLine({ elsewhere, leading, shown = true }: { elsewhere: ConversationElsewhere; leading?: ReactNode; shown?: boolean }) {
  const said = useElsewhereText(elsewhere.held);
  if (!shown || (!leading && !said)) return null;
  return <div className={`conversation-elsewhere-notice${said?.working ? " working" : ""}`}>
    {leading}
    {said && <span className="conversation-elsewhere-held" role="status"><AppIcon name={said.icon} size={14} /><span>{said.text}</span>
      <span className="muted-text">{said.detail}</span></span>}
  </div>;
}
