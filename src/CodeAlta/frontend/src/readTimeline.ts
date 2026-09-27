import { workspace } from "#neoastra";
import type { TimelinePage } from "./history";

export const readTimeline: typeof workspace.historyTail = async (request, options): Promise<TimelinePage> => {
  const value = await workspace.historyTimeline(request, options);
  return { ...value.page, revision: value.revision, sources: value.sources };
};
