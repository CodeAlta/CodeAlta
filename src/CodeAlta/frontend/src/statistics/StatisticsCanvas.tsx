import { Tab, Tabs } from "@blueprintjs/core";
import type { ReactNode } from "react";
import type { StatisticsApi, StatisticsContext } from "./api";
import { FrameBar } from "./FrameBar";
import { FirstTimeCard, HistoryBar } from "./HistoryBar";
import { pageIds, type PageId } from "./frame";
import { historyView } from "./history";
import { pageLabel } from "./labels";
import { StatisticsProvider, useStatistics } from "./runtime";
import { useText } from "./text";
import { ActivityPage } from "./pages/ActivityPage";
import { AgentsPage } from "./pages/AgentsPage";
import { CodePage } from "./pages/CodePage";
import { CostPage } from "./pages/CostPage";
import { HealthPage } from "./pages/HealthPage";
import { ModelsPage } from "./pages/ModelsPage";
import { OverviewPage } from "./pages/OverviewPage";
import { ProjectsPage } from "./pages/ProjectsPage";
import { PromptsPage } from "./pages/PromptsPage";
import { SessionsPage } from "./pages/SessionsPage";
import { ToolsPage } from "./pages/ToolsPage";
import "./statistics.css";

const pages: Record<PageId, () => ReactNode> = {
  overview: () => <OverviewPage />, activity: () => <ActivityPage />, models: () => <ModelsPage />, cost: () => <CostPage />, tools: () => <ToolsPage />, prompts: () => <PromptsPage />,
  agents: () => <AgentsPage />, code: () => <CodePage />, projects: () => <ProjectsPage />, sessions: () => <SessionsPage />, health: () => <HealthPage />,
};

function Canvas() {
  const { t } = useText();
  const { frame, dispatch, status, visible } = useStatistics();
  const view = historyView(status);
  return <div className="statistics-canvas" data-visible={visible} data-history={view}>
    <FrameBar />
    <HistoryBar />
    {view === "choice" && status
      ? <div className="stats-page stats-page-first"><FirstTimeCard status={status} /></div>
      : <Tabs id="statistics-pages" className="stats-tabs" selectedTabId={frame.page} onChange={next => dispatch({ type: "page", page: next as PageId })} renderActiveTabPanelOnly animate={false}
        aria-label={t("Statistics pages")}>
        {pageIds.map(page => <Tab key={page} id={page} disabled={false} title={pageLabel(t, page)} panel={<div className="stats-page" data-page={page}>{pages[page]()}</div>} />)}
      </Tabs>}
  </div>;
}

/**
 * The Statistics canvas: a bar with the period, the frequency, the comparison and the filters, the pages as tabs, and the
 * state of the reading of the history. It asks `api` for every number and draws what comes back; it keeps what it needs
 * between reloads under `context.instanceId`, and stops asking while `context.visible` is false.
 *
 * Mount it once per canvas instance. It is safe under React StrictMode.
 */
export function StatisticsCanvas({ api, context }: Readonly<{ api: StatisticsApi; context: StatisticsContext }>) {
  return <StatisticsProvider api={api} context={context}><Canvas /></StatisticsProvider>;
}
