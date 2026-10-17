import { createContext, type ReactNode } from "react";

/**
 * What the shell shows around every composer below a provider: `context` above the editor on the right (where the
 * session works: the Workspace chip), `status` beside it (for example the MCP servers), `footer` above the surface
 * (what plugins add there) and `subAgents` below the editor (the sub-agents at work under the session); `promptCover`
 * in the place of the text of the prompt, which stays under it with its draft, until it is collapsed (a Claude Code
 * conversation not in CodeAlta yet, or one another Claude process holds).
 */
export type ComposerChromeValue = Readonly<{ context?: ReactNode; status?: ReactNode; footer?: ReactNode; subAgents?: ReactNode; promptCover?: ReactNode }>;
export const ComposerChrome = createContext<ComposerChromeValue>({});
