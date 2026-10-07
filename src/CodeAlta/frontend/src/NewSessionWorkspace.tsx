import { SessionWidthGrips } from "./SessionWidthGrips";
import { useMemo, type ReactNode } from "react";
import type { WorkspaceProject } from "#neoastra";
import font from "../../../CodeAlta.Tui/Assets/3d.flf?raw";
import { ComposerSplitter, useComposerLayout } from "./ComposerLayout";
import { ComposerChrome, type ComposerChromeValue } from "./composerChrome";
import { useShellLanguage } from "./shellLanguage";
import { welcomeLogo } from "./welcomeLogo";

const noChrome: ComposerChromeValue = {};

export function NewSessionWorkspace({ project, worktree = false, preferredHeight, onHeight, chrome, children }: {
  project?: WorkspaceProject; preferredHeight?: number; onHeight: (height: number | undefined) => void; children: ReactNode;
  /** The next session of the project works in a new git worktree, not in the folder of the project. */
  worktree?: boolean;
  /** Shown around the composer: the working folder, the MCP status. */
  chrome?: ComposerChromeValue;
}) {
  const { t } = useShellLanguage();
  const logo = useMemo(() => welcomeLogo(font), []);
  const composer = useComposerLayout(preferredHeight, onHeight);
  return <div className="session-workspace blank-project" data-active="true" ref={composer.workspaceRef}>
    <div className="timeline-scroll new-session-welcome">
      <div className="welcome-content">
        <div className="blank-project-logo" role="img" aria-label="CodeAlta">
          <LogoWord text={logo.code} /><LogoWord text={logo.alta} className="logo-alta" />
        </div>
        <p className="welcome-subtitle">{project ? worktree ? t("Next session will start in {project}, in a new git worktree.", { project: project.name })
          : t("Next session will start in {project} from folder {folder}.", { project: project.name, folder: project.path })
          : t("Ready for a new chat, in no project.")}</p>
        <p>{project ? t("Use the prompt below to start a new session for {project}.", { project: project.name })
          : t("Use the prompt below to start a new chat.")}</p>
        <p>{t("Switch projects in the sidebar before sending if you want a different scope.")}</p>
        <p>{t("Reopen any session tab to continue previous work.")}</p>
      </div>
    </div>
    <div className="composer-resize-bar" ref={composer.barRef}><ComposerSplitter {...composer.splitter} /></div>
    <div ref={composer.regionRef} className={`composer-region${composer.height === undefined ? "" : " resized"}`}
      style={composer.height === undefined ? undefined : { height: composer.height }}><SessionWidthGrips /><ComposerChrome.Provider value={chrome ?? noChrome}>{children}</ComposerChrome.Provider></div>
  </div>;
}

function LogoWord({ text, className }: { text: string; className?: string }) {
  // Box-drawing glyphs may come from a proportional fallback font on the WebView.
  // Allocate FIGlet cells explicitly so both words share the same columns/baseline.
  const rows = text.split("\n"), columns = Math.max(...rows.map(row => Array.from(row).length));
  return <div className={`welcome-logo-word${className ? ` ${className}` : ""}`} aria-hidden="true">
    {rows.map((row, index) => <div className="welcome-logo-row" key={index} style={{ width: `${columns}ch` }}>
      {Array.from(row, (character, column) => <span key={column}>{character}</span>)}
    </div>)}
  </div>;
}
