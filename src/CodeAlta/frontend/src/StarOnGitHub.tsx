import { useContext } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { MarkdownLinksContext } from "./MarkdownContent";
import { useShellLanguage } from "./shellLanguage";
import "./starOnGitHub.css";

/** The repository of CodeAlta on GitHub, where a person can star the project. */
export const codeAltaRepositoryUrl = "https://github.com/CodeAlta/CodeAlta";

/**
 * A small invitation to star CodeAlta on GitHub: an outlined button, with a heart and the mark of a link to the web, that opens the
 * repository in the browser. Drawn only where the window can open a link.
 */
export function StarOnGitHub() {
  const { t } = useShellLanguage();
  const openLink = useContext(MarkdownLinksContext);
  if (!openLink) return null;
  return <Button className="star-on-github" variant="outlined" size="small"
    icon={<AppIcon name="heart" size={14} className="star-on-github-heart" aria-hidden="true" />}
    endIcon={<AppIcon name="openExternal" size={12} className="star-on-github-external" aria-hidden="true" />} title={codeAltaRepositoryUrl}
    text={t("Like CodeAlta? Star it on GitHub")} onClick={() => openLink(codeAltaRepositoryUrl, null)} />;
}
