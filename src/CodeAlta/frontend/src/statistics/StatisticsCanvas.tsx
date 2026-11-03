import { Tab, Tabs } from "@blueprintjs/core";
import { useEffect, useId, useRef, useState, type ReactNode, type RefObject } from "react";
import type { StatisticsApi, StatisticsContext } from "./api";
import { Choice } from "./blocks";
import type { FleetStatisticsApi } from "./fleet";
import { FleetScope } from "./FleetScope";
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
import { ScriptsPage } from "./pages/ScriptsPage";
import { SessionsPage } from "./pages/SessionsPage";
import { ToolsPage } from "./pages/ToolsPage";

// The style of the canvas (statistics.css) is part of the stylesheet of the page: a module of a plugin brings none.

const pages: Record<PageId, () => ReactNode> = {
  overview: () => <OverviewPage />, activity: () => <ActivityPage />, models: () => <ModelsPage />, cost: () => <CostPage />, tools: () => <ToolsPage />, prompts: () => <PromptsPage />,
  agents: () => <AgentsPage />, scripts: () => <ScriptsPage />, code: () => <CodePage />, projects: () => <ProjectsPage />, sessions: () => <SessionsPage />, health: () => <HealthPage />,
};

/**
 * The row of the pages scrolls when the canvas is narrow: it says on which side there is more (`data-start`, `data-end`, which the
 * style fades) and brings the page in front into view.
 */
function usePageTabsEdges(root: RefObject<HTMLElement | null>, page: PageId, shown: boolean) {
  useEffect(() => {
    const list = shown ? root.current?.querySelector<HTMLElement>(".stats-tabs > .bp6-tab-list") : null;
    if (!list) return;
    const update = () => {
      list.dataset.start = String(list.scrollLeft > 1);
      list.dataset.end = String(list.scrollLeft + list.clientWidth < list.scrollWidth - 1);
    };
    list.querySelector<HTMLElement>("[aria-selected=true]")?.scrollIntoView({ block: "nearest", inline: "nearest" });
    update();
    list.addEventListener("scroll", update, { passive: true });
    const observer = new ResizeObserver(update);
    observer.observe(list);
    return () => { list.removeEventListener("scroll", update); observer.disconnect(); };
  }, [root, page, shown]);
}

// Whether "My fleet" is offered: this computer is linked to a server. Asked once the canvas shows; a profile without a server never sees it.
function useFleetAvailable(fleet: FleetStatisticsApi | null | undefined, visible: boolean): boolean {
  const [available, setAvailable] = useState(false);
  useEffect(() => {
    if (!fleet || !visible) return;
    const controller = new AbortController();
    fleet.available(controller.signal).then(value => { if (!controller.signal.aborted) setAvailable(value); }, () => { });
    return () => controller.abort();
  }, [fleet, visible]);
  return available;
}

function Canvas({ fleet }: Readonly<{ fleet?: FleetStatisticsApi | null }>) {
  const { t } = useText();
  const { frame, dispatch, status, visible, header, context } = useStatistics();
  const view = historyView(status);
  const showCharts = view !== "choice" || (status?.savedSessions ?? 0) > 0;
  const root = useRef<HTMLDivElement>(null);
  // Several canvases stay mounted in one window (the one of the application, the one of a project): the ids of the tabs are per canvas.
  const tabsId = useId();
  // The scope of the canvas of the application: this machine, or every machine of the person through the server.
  const [scope, setScope] = useState<"machine" | "fleet">("machine");
  const offersFleet = useFleetAvailable(context.projectId || context.sessionId ? null : fleet, visible);
  const inFleet = offersFleet && scope === "fleet" && !!fleet;
  usePageTabsEdges(root, frame.page, showCharts && !inFleet);
  return <div className="statistics-canvas" ref={root} data-visible={visible} data-history={view} data-scope={inFleet ? "fleet" : "machine"}>
    {offersFleet && <div className="stats-scope">
      <Choice label={t("Statistics scope")} value={scope} onChange={setScope} options={[{ value: "machine", label: t("This machine") }, { value: "fleet", label: t("My fleet") }]} />
    </div>}
    {inFleet ? <FleetScope api={fleet} /> : <>
      <FrameBar />
      <HistoryBar />
      {(header?.coverage.unavailableSavedMetrics?.length ?? 0) > 0 && <p role="status" className="stats-page">
        {t("Some saved sessions predate current metric definitions. Unrecorded metrics are unavailable, not observed zeros.")}
      </p>}
      {view === "choice" && status && <div className="stats-page stats-page-first"><FirstTimeCard status={status} /></div>}
      {showCharts && <Tabs id={tabsId} className="stats-tabs" selectedTabId={frame.page} onChange={next => dispatch({ type: "page", page: next as PageId })} renderActiveTabPanelOnly animate={false}
          aria-label={t("Statistics pages")}>
          {pageIds.map(page => <Tab key={page} id={page} disabled={false} title={pageLabel(t, page)} panel={<div className="stats-page" data-page={page}>{pages[page]()}</div>} />)}
        </Tabs>}
    </>}
  </div>;
}

/**
 * The Statistics canvas: a bar with the period, the frequency, the comparison and the filters, the pages as tabs, and the
 * state of the reading of the history. It asks `api` for every number and draws what comes back; it keeps what it needs
 * between reloads under `context.instanceId`, and stops asking while `context.visible` is false.
 *
 * Mount it once per canvas instance. It is safe under React StrictMode. With `fleet`, the canvas of the application offers the scope
 * "My fleet" once the binding says a server is linked.
 */
export function StatisticsCanvas({ api, context, fleet }: Readonly<{ api: StatisticsApi; context: StatisticsContext; fleet?: FleetStatisticsApi | null }>) {
  return <StatisticsProvider api={api} context={context}><Canvas fleet={fleet} /></StatisticsProvider>;
}
