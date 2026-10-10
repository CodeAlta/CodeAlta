import { useContext } from "react";
import { NonIdealState } from "@blueprintjs/core";
import { useAlta } from "../pluginScript/PluginScript";
import { useTheme, useVisible } from "../pluginScript/codealta";
import { useShellLanguage } from "../shellLanguage";
import { LandingCanvas } from "./LandingCanvas";
import { LandingShellContext } from "./landingShell";

// The module of the landing page, the one the built-in landing plugin names with `PluginScript.App("landing")`: it gives the page
// what the shell of the window lends it and what its tab is, and draws it.

/** The landing page, for the window to draw in a tab. */
export default function Canvas() {
  const alta = useAlta();
  const visible = useVisible();
  const theme = useTheme();
  const shell = useContext(LandingShellContext);
  const { t } = useShellLanguage();
  // A tab drawn where no shell lends anything (a detached view) has nothing to show.
  if (!shell) return <NonIdealState className="landing-unavailable" title={t("Welcome")} />;
  return <LandingCanvas shell={shell} visible={visible} dark={theme.dark}
    openCanvas={request => alta.host.openCanvas(request.canvasId, { pluginKey: request.pluginKey, projectId: request.projectId ?? undefined, key: request.key ?? undefined })} />;
}
