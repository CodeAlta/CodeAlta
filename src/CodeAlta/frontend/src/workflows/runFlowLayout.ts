// Places the nodes of the graph of a run, left to right, with dagre. Part of the chunk of the graph (WorkflowFlow.tsx), which
// the application loads when the first graph is shown; pure, so that the tests lay a graph out without a page.
import dagre from "@dagrejs/dagre";
import { flowMetrics, type FlowElements, type FlowMode } from "./runFlow";

/** Where each node goes (its top left corner), and the size of the whole graph. */
export type FlowLayout = Readonly<{ positions: ReadonlyMap<string, Readonly<{ x: number; y: number }>>; width: number; height: number }>;

/** Lays the nodes out in ranks from the start of the run, left to right (or top to bottom): a step one rank after the steps it runs after. */
export function layoutFlow(elements: FlowElements, mode: FlowMode): FlowLayout {
  const metrics = flowMetrics[mode];
  const graph = new dagre.graphlib.Graph();
  const vertical = elements.direction === "TB";
  graph.setGraph({ rankdir: elements.direction, nodesep: metrics.nodesep, ranksep: vertical ? metrics.verticalRanksep : metrics.ranksep, marginx: 0, marginy: 0 });
  graph.setDefaultEdgeLabel(() => ({}));
  for (const node of elements.nodes) graph.setNode(node.id, { width: node.width, height: node.height });
  for (const edge of elements.edges) if (graph.hasNode(edge.source) && graph.hasNode(edge.target)) graph.setEdge(edge.source, edge.target);
  dagre.layout(graph);
  const positions = new Map<string, { x: number; y: number }>();
  let width = 0, height = 0;
  for (const node of elements.nodes) {
    const placed = graph.node(node.id);
    const x = Math.round(placed.x - node.width / 2), y = Math.round(placed.y - node.height / 2);
    positions.set(node.id, { x, y });
    width = Math.max(width, x + node.width);
    height = Math.max(height, y + node.height);
  }
  return { positions, width, height };
}
