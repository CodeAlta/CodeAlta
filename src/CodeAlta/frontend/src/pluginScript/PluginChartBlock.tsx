import { Chart } from "../charts/Chart";
import { sanitizeChartOption, type ChartOption } from "../charts/sanitize";
import { useShellLanguage } from "../shellLanguage";

/** What an `alta-chart` block of a fragment holds: the checked option and its label, or the reason there is none. */
export type PluginChartSource = Readonly<{ option: ChartOption | null; label: string }>;

/**
 * Reads the JSON option of an `alta-chart` block. An option that is not JSON, or is not data only (a function, a link, a toolbox), is
 * `null`: the block then shows a short message and nothing else, and nothing throws.
 */
export function readPluginChart(optionText: string | null, label: string | null): PluginChartSource {
  const text = (label ?? "").slice(0, 256);
  if (!optionText) return { option: null, label: text };
  try {
    const checked = sanitizeChartOption(JSON.parse(optionText));
    return { option: checked.ok ? checked.option : null, label: text };
  } catch {
    return { option: null, label: text };
  }
}

/** The chart of an `alta-chart` block, drawn with the colors of the window; a quiet message when its option is not valid. */
export function PluginChartBlock({ source, visible }: { source: PluginChartSource; visible: boolean }) {
  const { t } = useShellLanguage();
  if (!source.option) return <span className="bp6-text-muted plugin-chart-invalid">{t("This chart could not be drawn.")}</span>;
  return <Chart option={source.option} ariaLabel={source.label || t("Chart")} visible={visible} />;
}
