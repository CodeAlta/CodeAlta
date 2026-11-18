import { AppIcon, type IconName } from "./AppIcon";
import { CanvasIcon } from "./canvases/CanvasIcon";
import { ProviderIcon } from "./ProviderIcon";
import { isAutomationsTab, isBookmarksTab, isCanvasTab, isCanvasesTab, isChangesTab, isClaudeCodeTab, isDocumentationTab, isIssuesTab, isPluginTab, isSkillTab, isTerminalTab, isWorkflowRunTab,
  isWorkflowsTab, isWorkItemsTab, type FileTab } from "./fileTabs";

/** The icon of the tab of a project's view in the strip, and the tone it takes. A terminal whose shell ended is muted. */
export function fileTabLook(file: FileTab, running = true): Readonly<{ icon: IconName; tone: string }> {
  return isTerminalTab(file) ? { icon: "terminal", tone: running ? "green" : "muted" }
    : isChangesTab(file) ? { icon: "changes", tone: "orange" }
    : isAutomationsTab(file) ? { icon: "automation", tone: "gold" }
    : isWorkflowsTab(file) || isWorkflowRunTab(file) ? { icon: "workflow", tone: "blue" }
    : isWorkItemsTab(file) ? { icon: "task", tone: "teal" }
    : isIssuesTab(file) ? { icon: "issueOpen", tone: "green" }
    : isCanvasesTab(file) ? { icon: "canvases", tone: "purple" }
    : isDocumentationTab(file) ? { icon: "documentation", tone: "azure" }
    : isBookmarksTab(file) ? { icon: "bookmark", tone: "gold" }
    : isPluginTab(file) ? { icon: "plugin", tone: "purple" }
    : isSkillTab(file) ? { icon: "skill", tone: "teal" } : { icon: "code", tone: "azure" };
}

/** The icon in front of the tab of a project's view: in the strip and in the tab switcher. */
export function FileTabIcon({ file, running = true }: Readonly<{ file: FileTab; running?: boolean }>) {
  if (isCanvasTab(file)) return <span className="file-tab-icon" data-file-tone="purple"><CanvasIcon name={file.icon} pluginKey={file.pluginKey} size={14} /></span>;
  // Not a session of CodeAlta yet: the logo of its provider, dimmed, as in the list.
  if (isClaudeCodeTab(file)) return <span className="file-tab-icon claude-code-tab-icon"><ProviderIcon providerKey={file.icon ?? null} size={14} fallback="assistant" /></span>;
  const look = fileTabLook(file, running);
  return <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>;
}
