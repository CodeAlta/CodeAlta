export type ShortcutAction =
  | "openProject" | "focusProjects" | "focusSessions" | "focusPrompt" | "focusSearch" | "browseSessions"
  | "nextProject" | "previousProject" | "nextSession" | "previousSession"
  | "nextTab" | "previousTab" | "closeTab" | "reopenTab"
  | "settings" | "providers" | "models" | "prompts" | "context" | "plugins"
  | "toggleNotes" | "help" | "escape" | "expandPrompt" | "renameProject" | "sessionInfo" | "reminders"
  | "messagePrevious" | "messageNext" | "messageFirst" | "messageLatest" | "compact";

export type ShortcutKey = Readonly<{ key: string; ctrlKey?: boolean; altKey?: boolean; shiftKey?: boolean; metaKey?: boolean;
  isComposing?: boolean; keyCode?: number; defaultPrevented?: boolean; repeat?: boolean }>;

// Monaco can target an EditContext-backed div instead of a textarea/contenteditable.
export const workspaceEditingSelector = "input, textarea, select, [contenteditable]:not([contenteditable='false']), .prompt-editor, .monaco-editor";
