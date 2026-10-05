import { createContext, type ReactNode } from "react";

/**
 * What the shell shows around every composer below a provider: `context` after the selection chip (the
 * working folder and its git branch), `status` at the end of the status line (for example the MCP servers)
 * and `footer` above the status line (what plugins add there).
 */
export type ComposerChromeValue = Readonly<{ context?: ReactNode; status?: ReactNode; footer?: ReactNode }>;
export const ComposerChrome = createContext<ComposerChromeValue>({});
