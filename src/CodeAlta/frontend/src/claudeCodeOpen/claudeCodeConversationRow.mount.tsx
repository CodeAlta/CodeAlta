// Disposable page: the Session Info window of a Claude Code session, whose reader of the conversation of the CLI the test
// answers. Driven by claudeCodeConversationRow.browser.test.ts through `window.conversationFixture`.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { SessionClaudeCodeConversationResult } from "#neoastra";
import { SessionInfoDialog } from "../SessionInfoDialog";
import type { SessionInfoView } from "../sessionInfo";
import { ClaudeCodeConversationRow } from "./ClaudeCodeConversationRow";

const root = createRoot(document.getElementById("root")!);
const info: SessionInfoView = { id: "session-1", title: "Fix the parser", titleTruncated: false, scope: "Project: CodeAlta", scopeWarning: null,
  path: "C:\\dev\\codealta", worktree: null, provider: "claude-code", updatedAt: null, createdAt: null, canCopyId: true, lineage: null };
const state = {
  reads: 0,
  copied: [] as string[],
  /** What the host answers: the conversation, or null for a session that has none. */
  answer: null as SessionClaudeCodeConversationResult | null,
};
const conversation = (held: SessionClaudeCodeConversationResult["held"] = null): SessionClaudeCodeConversationResult => ({
  status: "ok", epoch: "epoch", sessionId: "session-1", cliSessionId: "5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01",
  resumeCommand: "claude --resume 5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01", workingDirectory: "C:\\dev\\codealta", held,
});
const read = async () => { state.reads++; return state.answer; };

const fixture = {
  state,
  conversation,
  /** The Session Info window, with a reader of the conversation when the session may have one. */
  render(withReader: boolean) {
    flushSync(() => root.render(createElement(StrictMode, null,
      createElement(SessionInfoDialog, { info, demo: false, onClose: () => { }, conversation: withReader ? read : null }))));
  },
  /** The row alone, with a clipboard the test reads. */
  renderRow() {
    flushSync(() => root.render(createElement(StrictMode, null, createElement("dl", null,
      createElement(ClaudeCodeConversationRow, { read, write: async (text: string) => { state.copied.push(text); } })))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { conversationFixture: fixture });
