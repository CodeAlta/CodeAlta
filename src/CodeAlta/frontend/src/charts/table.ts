/** The data of a chart as a table: a header and rows of text, numbers or nothing. */
export type ChartTable = Readonly<{ columns: readonly string[]; rows: readonly (readonly (string | number | null)[])[] }>;

type Dict = Record<string, unknown>;
const isDict = (value: unknown): value is Dict => typeof value === "object" && value !== null && !Array.isArray(value);
const asList = (value: unknown): unknown[] => Array.isArray(value) ? value : value === undefined || value === null ? [] : [value];
const cell = (value: unknown): string | number | null =>
  typeof value === "number" ? (Number.isFinite(value) ? value : null) : typeof value === "string" ? value : value instanceof Date ? value.toISOString()
    : typeof value === "boolean" ? String(value) : null;
// The value a data item stands for: the item, its `value`, or the last of its coordinates.
const valueOf = (item: unknown): unknown => isDict(item) ? valueOf(item.value) : Array.isArray(item) ? item[item.length - 1] : item;
// The coordinates of a data item: its array, or the array in its `value`.
const coordinates = (item: unknown): unknown[] => asList(isDict(item) ? item.value : item);
const nameOf =(item: unknown): string => isDict(item) && item.name !== undefined ? String(item.name) : "";
const boxColumns = ["min", "Q1", "median", "Q3", "max"];
const wholeTypes = new Set(["pie", "funnel", "sunburst", "treemap", "sankey", "gauge"]);

/**
 * The same numbers as the chart draws, found in its option: the categories of an axis against one column per
 * series, the points of a time or value axis, the slices of a pie, the leaves of a treemap, a `dataset`.
 * An empty table when the option holds nothing it can read.
 */
export function tableFromOption(option: Readonly<Record<string, unknown>>): ChartTable {
  const dataset = asList(option.dataset).find(isDict);
  const series = asList(option.series).filter(isDict);
  const hasSeriesData = series.some(item => item.data !== undefined);
  if (dataset && !hasSeriesData) return datasetTable(dataset);
  if (series.length === 0) return { columns: [], rows: [] };
  if (series.every(item => wholeTypes.has(String(item.type)))) return wholeTable(series);
  const categories = categoriesOf(option);
  return categories ? categoryTable(categories, series) : pointTable(series);
}

function datasetTable(dataset: Dict): ChartTable {
  const source = asList(dataset.source);
  if (source.length === 0) return { columns: [], rows: [] };
  if (isDict(source[0])) {
    const columns = Object.keys(source[0]);
    return { columns, rows: source.map(row => columns.map(name => cell(isDict(row) ? row[name] : null))) };
  }
  const rows = source.map(row => asList(row).map(cell));
  if (dataset.sourceHeader === false || !rows[0].every(value => typeof value === "string")) {
    return { columns: rows[0].map((_, index) => String(index + 1)), rows };
  }
  return { columns: rows[0].map(String), rows: rows.slice(1) };
}

function categoriesOf(option: Dict): unknown[] | null {
  for (const axis of [...asList(option.xAxis), ...asList(option.yAxis)]) {
    if (isDict(axis) && axis.type === "category" && Array.isArray(axis.data)) return axis.data.map(item => isDict(item) ? item.value ?? item.name : item);
  }
  return null;
}

function wholeTable(series: Dict[]): ChartTable {
  const rows: (string | number | null)[][] = [];
  const walk = (items: unknown[], path: string, label: string) => {
    for (const item of items) {
      const name = path ? `${path} / ${nameOf(item)}` : nameOf(item);
      if (isDict(item) && Array.isArray(item.children) && item.children.length > 0) walk(item.children, name, label);
      else rows.push([name, cell(valueOf(item)), ...(series.length > 1 ? [label] : [])]);
    }
  };
  for (const item of series) walk(asList(item.data), "", String(item.name ?? ""));
  return { columns: ["", series.length === 1 ? String(series[0].name ?? "") : "value", ...(series.length > 1 ? [""] : [])], rows };
}

function categoryTable(categories: unknown[], series: Dict[]): ChartTable {
  const columns: string[] = [""];
  const parts: ((index: number) => (string | number | null)[])[] = [];
  series.forEach((item, position) => {
    const name = String(item.name ?? `${position + 1}`);
    const data = asList(item.data);
    if (item.type === "boxplot") {
      boxColumns.forEach(stat => columns.push(`${name} ${stat}`));
      parts.push(index => boxColumns.map((_, at) => cell(coordinates(data[index])[at])));
    } else {
      columns.push(name);
      parts.push(index => [cell(valueOf(data[index]))]);
    }
  });
  const rows = categories.map((category, index) => [cell(category), ...parts.flatMap(part => part(index))]);
  return { columns, rows };
}

function pointTable(series: Dict[]): ChartTable {
  // Series of [x, value] pairs share rows by x; a series of triples (a heat map) lists x, y and value.
  const triples = series.find(item => asList(item.data).some(point => coordinates(point).length >= 3));
  if (triples) {
    const rows = series.flatMap(item => asList(item.data).map(point => coordinates(point).map(cell)));
    return { columns: ["x", "y", String(triples.name ?? "value")], rows: rows.map(row => [row[0] ?? null, row[1] ?? null, row[2] ?? null]) };
  }
  const columns = [""], byX = new Map<string, (string | number | null)[]>(), order: string[] = [];
  series.forEach((item, position) => {
    columns.push(String(item.name ?? `${position + 1}`));
    for (const point of asList(item.data)) {
      const pair = coordinates(point);
      const x = pair.length >= 2 ? cell(pair[0]) : nameOf(point);
      const key = String(x);
      let row = byX.get(key);
      if (!row) { row = [x, ...series.map(() => null)]; byX.set(key, row); order.push(key); }
      row[position + 1] = cell(pair.length >= 2 ? pair[1] : valueOf(point));
    }
  });
  return { columns, rows: order.map(key => byX.get(key)!) };
}

/** The table as text a spreadsheet pastes into cells: tab-separated, one line per row, the header first. */
export function tableToText(table: ChartTable): string {
  const clean = (value: string | number | null) => value === null ? "" : String(value).replace(/[\t\r\n]+/g, " ");
  return [table.columns, ...table.rows].map(row => row.map(clean).join("\t")).join("\n");
}
