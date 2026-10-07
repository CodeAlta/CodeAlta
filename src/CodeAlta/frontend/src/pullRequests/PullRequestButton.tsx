import { useState } from "react";
import { Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { pullRequestPrompts, PullRequestPromptItem } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { askPluginComposer } from "../pluginUi";
import { useShellLanguage } from "../shellLanguage";

/** The part of the host the button uses; a test gives its own. */
export type PullRequestPromptsApi = Pick<typeof pullRequestPrompts, "list">;

/**
 * Asks the session to create a pull request for its work. It is one small button of the strip under the prompt:
 * its menu lists the kinds of pull request (the one that ships with CodeAlta, and those of the user and of the
 * project), and choosing one sends the session the instructions of that kind. A session that is working is not
 * interrupted: the kinds are offered once it is idle.
 */
export function PullRequestButton({ api, epoch, projectId, sessionId, onOpenSettings, onNotice }: {
  api: PullRequestPromptsApi; epoch: string; projectId: string; sessionId: string;
  onOpenSettings?: () => void;
  /** Says why the instructions were not sent. */
  onNotice: (message: string) => void;
}) {
  const { t } = useShellLanguage();
  const [items, setItems] = useState<readonly PullRequestPromptItem[] | null>(null);
  const [busy, setBusy] = useState(false);
  // Read when the menu opens: a kind added a moment ago is there, and the session says whether it works.
  function load() {
    setItems(null);
    setBusy(askPluginComposer("state", sessionId).state?.busy ?? false);
    void api.list({ expectedEpoch: epoch, projectId, all: false }, { timeoutMilliseconds: 15_000 })
      .then(reply => setItems(reply.status === "ok" ? reply.items : []), () => setItems([]));
  }
  function send(item: PullRequestPromptItem) {
    const state = askPluginComposer("state", sessionId).state;
    if (state?.busy) { onNotice(t("The session is working. Ask for the pull request once it is idle.")); return; }
    // The instructions go through the prompt: what is being written there is not thrown away for them.
    if (state?.draftText.trim()) { onNotice(t("Send or clear the prompt you are writing first.")); return; }
    if (!askPluginComposer("send", sessionId, item.content).result) onNotice(t("This session cannot take it right now. Try again in a moment."));
  }
  return <PopoverNext placement="top-start" onOpening={load} content={<Menu className="pull-request-menu" aria-label={t("Create a pull request")}>
    <MenuDivider title={t("Create a pull request")} />
    {items === null ? <MenuItem disabled text={<ActivitySpinner size={13} />} />
      : items.length === 0 ? <MenuItem disabled text={t("No instructions could be read.")} />
      : items.map(item => <MenuItem key={item.id} icon={<AppIcon name="pullRequest" size={15} />} text={item.name} title={item.description ?? undefined} disabled={busy}
        labelElement={item.source !== "builtin" ? <span className="pull-request-source">{t(item.source === "project" ? "Project" : "Global")}</span> : undefined} onClick={() => send(item)} />)}
    {busy && <MenuItem disabled icon={<AppIcon name="info" size={15} />} text={t("Available once the session is idle.")} />}
    {onOpenSettings && <><MenuDivider /><MenuItem icon={<AppIcon name="settings" size={15} />} text={t("Pull request instructions…")} onClick={onOpenSettings} /></>}
  </Menu>}>
    <button type="button" className="project-context-pull" title={t("Create a pull request")} aria-label={t("Create a pull request")} aria-haspopup="menu">
      <AppIcon name="pullRequest" size={13} /></button>
  </PopoverNext>;
}
