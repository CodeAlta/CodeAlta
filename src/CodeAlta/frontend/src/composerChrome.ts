import { createContext, type ReactNode } from "react";

/**
 * What the shell shows around every composer below a provider: `context` after the selection chip (the
 * working folder and its git branch) and `status` at the end of the status line (for example the MCP servers).
 */
export type ComposerChromeValue = Readonly<{ context?: ReactNode; status?: ReactNode }>;
export const ComposerChrome = createContext<ComposerChromeValue>({});
