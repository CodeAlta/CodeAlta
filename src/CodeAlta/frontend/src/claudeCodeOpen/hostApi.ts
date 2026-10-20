import { sessionOperations } from "#neoastra";
import type { ClaudeCodeOpenApi } from "./claudeCodeOpen";

/** The calls of the host of the window that open a Claude Code session elsewhere. A release can wait for a process to end. */
export const claudeCodeOpenHostApi: ClaudeCodeOpenApi = Object.freeze({
  conversation: request => sessionOperations.claudeCodeConversation(request, { timeoutMilliseconds: 30_000 }),
  release: request => sessionOperations.releaseClaudeCodeConversation(request, { timeoutMilliseconds: 90_000 }),
  openInVsCode: request => sessionOperations.openInVsCode(request, { timeoutMilliseconds: 90_000 }),
});
