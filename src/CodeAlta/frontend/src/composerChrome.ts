import { createContext, type ReactNode } from "react";

/**
 * What the shell shows around every composer below a provider: `context` above the editor on the right (where the
 * session works: the Workspace chip), `status` beside it (for example the MCP servers), `footer` above the surface
 * (what plugins add there) and `subAgents` below the editor (the sub-agents at work under the session).
 */
export type ComposerChromeValue = Readonly<{ context?: ReactNode; status?: ReactNode; footer?: ReactNode; subAgents?: ReactNode }>;
export const ComposerChrome = createContext<ComposerChromeValue>({});
