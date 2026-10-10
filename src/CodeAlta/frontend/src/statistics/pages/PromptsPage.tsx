import { useMemo, useState } from "react";
import { Block, Choice } from "../blocks";
import { usePageColors } from "../colors";
import { senderLabel } from "../labels";
import { DistributionChart, SeriesChart } from "../pageKit";
import { scatterOption } from "../options";
import { StatChart } from "../StatChart";
import { useDistribution, useRuns, useSeries } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import { combineSeries, firstQuery } from "./shared";
import type { SeriesLine } from "../types";

// Prompts: how much do I write, and how much do agents write to each other?

type Cut = "sender" | "kind";

/** The Prompts page. */
export function PromptsPage() {
  const { t } = useText();
  const { fmt } = useStatistics();
  const colors = usePageColors();
  const [cut, setCut] = useState<Cut>("sender");
  const bySender = useSeries("prompts", "origin", { main: true, enabled: cut === "sender" });
  const byKind = useSeries("prompts", "prompt-kind", { enabled: cut === "kind" });
  const chars = useDistribution("prompt-chars", null, { extra: { comparison: "none" } });
  const words = useDistribution("prompt-words", null, { extra: { comparison: "none" } });
  const files = useSeries("prompt-files", null);
  const images = useSeries("prompt-images", null);
  const directories = useSeries("prompt-directories", null);
  const runs = useRuns("recent", { extra: { limit: 500, comparison: "none" } });
  const attachments = useMemo(() => files.data && images.data && directories.data ? combineSeries([
    { result: files.data, key: "files", label: t("Files") }, { result: images.data, key: "images", label: t("Images") }, { result: directories.data, key: "directories", label: t("Folders") }], "attachments") : null,
  [files.data, images.data, directories.data, t]);
  const shown = cut === "sender" ? bySender : byKind;
  const kindName = (line: SeriesLine) => {
    switch (line.key) {
      case "newturn": return t("New turn");
      case "steer": return t("Steer");
      case "queued": return t("Queued");
      case "answer": return t("Answer to a question");
      default: return line.label || line.key;
    }
  };
  const name = (line: SeriesLine) => cut === "sender" ? senderLabel(t, line.key) : kindName(line);
  const comes = useMemo(() => {
    const list = runs.data?.runs.filter(run => run.origin === "you") ?? [];
    if (list.length === 0) return null;
    const mean = (pick: (run: typeof list[number]) => number) => list.reduce((sum, run) => sum + pick(run), 0) / list.length;
    return { words: mean(run => run.answerWords), tools: mean(run => run.toolCalls), time: mean(run => run.durationMs), count: list.length,
      scatter: scatterOption(list.map(run => [Math.max(1, run.promptWords), Math.max(1, run.durationMs)] as const), t("Runs"), t("Words in the prompt"), t("Run time"), fmt, "ms") };
  }, [runs.data, t, fmt]);
  return <div className="stats-grid">
    <Block title={t("Prompts over time")} span={12} minHeight={280} query={shown} empty={shown.data !== undefined && shown.data.series.every(line => line.total === 0)}
      actions={<Choice label={t("Cut by")} value={cut} onChange={setCut} options={[{ value: "sender", label: t("Sender") }, { value: "kind", label: t("Kind") }]} />}>
      {shown.data && <SeriesChart result={shown.data} height={260} ariaLabel={cut === "sender" ? t("Prompts by sender") : t("Prompts by kind")} name={name} />}
    </Block>
    <Block title={t("Size of your prompts, in characters")} span={6} minHeight={240} query={chars} empty={chars.data !== undefined && chars.data.count === 0}>
      {chars.data && <DistributionChart result={chars.data} name={t("Prompts")} ariaLabel={t("Characters per prompt, on a logarithmic scale")} value={value => fmt.compact(value)} />}
    </Block>
    <Block title={t("Size of your prompts, in words")} span={6} minHeight={240} query={words} empty={words.data !== undefined && words.data.count === 0}>
      {words.data && <DistributionChart result={words.data} name={t("Prompts")} ariaLabel={t("Words per prompt, on a logarithmic scale")} value={value => fmt.compact(value)} />}
    </Block>
    <Block title={t("Attachments")} span={6} minHeight={240} query={firstQuery(files, images, directories)} empty={attachments !== null && attachments.series.every(line => line.total === 0)}>
      {attachments && <SeriesChart result={attachments} height={220} ariaLabel={t("Attachments")}
        colorOf={line => line.key === "files" ? colors.at(0) : line.key === "images" ? colors.at(4) : colors.at(2)} />}
    </Block>
    <Block title={t("What comes back")} caption={t("For a prompt of yours, on average")} span={6} minHeight={240} query={runs} empty={comes === null}>
      {comes && <div className="stats-back">
        <dl className="stats-facts">
          <div><dt>{t("Words written")}</dt><dd>{fmt.number(Math.round(comes.words))}</dd></div>
          <div><dt>{t("Tool calls")}</dt><dd>{fmt.number(Math.round(comes.tools))}</dd></div>
          <div><dt>{t("Active time")}</dt><dd>{fmt.duration(comes.time)}</dd></div>
        </dl>
        <StatChart option={comes.scatter.option} table={comes.scatter.table} ariaLabel={t("Run time against the size of the prompt")} height={170} group="prompts-scatter" />
      </div>}
    </Block>
  </div>;
}
