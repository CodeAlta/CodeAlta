import { useShellLanguage } from "./shellLanguage";

/**
 * The mark of a session whose turn ended (done, failed, or waiting for the user) while the user was not looking at it,
 * until they look at it: a dot of the accent color, apart from the green of a run and the yellow of a wait. The Explorer
 * shows it before the time of the row of the session, and the tab of the session shows it while another tab is in
 * front of its pane. It renders nothing for a session that has no mark.
 */
export function UnreadMark({ unread }: { unread: boolean }) {
  const { t } = useShellLanguage();
  if (!unread) return null;
  const label = t("New since you last looked");
  return <span className="session-unread" role="img" aria-label={label} title={label} />;
}
