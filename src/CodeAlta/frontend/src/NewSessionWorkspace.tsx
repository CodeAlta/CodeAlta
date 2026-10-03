import { useMemo, type ReactNode } from "react";
import type { WorkspaceProject } from "#neoastra";
import font from "../../../CodeAlta.Tui/Assets/3d.flf?raw";
import { ComposerSplitter, useComposerLayout } from "./ComposerLayout";
import { useShellLanguage } from "./shellLanguage";
import { welcomeLogo } from "./welcomeLogo";

export function NewSessionWorkspace({ project, preferredHeight, onHeight, children }: {
  project?: WorkspaceProject; preferredHeight?: number; onHeight: (height: number | undefined) => void; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const logo = useMemo(() => welcomeLogo(font), []);
  const composer = useComposerLayout(preferredHeight, onHeight);
  return <div className="session-workspace blank-project" data-active="true" ref={composer.workspaceRef}>
    <div className="timeline-scroll new-session-welcome">
      <div className="welcome-content">
        <div className="blank-project-logo" role="img" aria-label="CodeAlta">
          <pre aria-hidden="true">{logo.code}</pre><pre className="logo-alta" aria-hidden="true">{logo.alta}</pre>
        </div>
        <p className="welcome-subtitle">{project ? t("Next session will start in {project} from folder {folder}.", { project: project.name, folder: project.path })
          : t("Global workspace ready for a new session.")}</p>
        <p>{project ? t("Use the prompt below to start a new session for {project}.", { project: project.name })
          : t("Use the prompt below to start a new global session.")}</p>
        <p>{t("Switch projects in the sidebar before sending if you want a different scope.")}</p>
        <p>{t("Reopen any session tab to continue previous work.")}</p>
      </div>
    </div>
    <div className="composer-resize-bar" ref={composer.barRef}><ComposerSplitter {...composer.splitter} /></div>
    <div ref={composer.regionRef} className={`composer-region${composer.height === undefined ? "" : " resized"}`}
      style={composer.height === undefined ? undefined : { height: composer.height }}>{children}</div>
  </div>;
}
