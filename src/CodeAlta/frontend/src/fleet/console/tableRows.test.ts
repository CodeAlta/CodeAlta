import { test } from "node:test";
import assert from "node:assert/strict";
import { chosenCount, facetCounts, filterRows, sortRows, toggledFacet, toggledSort, type Facet } from "./tableRows";

type Row = Readonly<{ name: string; state: string; sessions: number | null; labels: readonly string[] }>;
const rows: Row[] = [
  { name: "node-10", state: "online", sessions: 3, labels: ["gpu"] },
  { name: "node-2", state: "offline", sessions: null, labels: [] },
  { name: "Mac", state: "online", sessions: 0, labels: ["gpu", "home"] },
];
const state: Facet<Row> = { id: "state", values: row => [row.state] };
const label: Facet<Row> = { id: "label", values: row => row.labels };

test("A column sorts texts as people read them, numbers in order, nothing last whatever the direction", () => {
  assert.deepEqual(sortRows(rows, row => row.name, "asc").map(row => row.name), ["Mac", "node-2", "node-10"]);
  assert.deepEqual(sortRows(rows, row => row.sessions, "asc").map(row => row.name), ["Mac", "node-10", "node-2"]);
  assert.deepEqual(sortRows(rows, row => row.sessions, "desc").map(row => row.name), ["node-10", "Mac", "node-2"], "a row without sessions stays last");
  assert.deepEqual(sortRows(rows, null, "asc"), rows, "without a column, the rows keep their order");
});

test("Clicking a header sorts ascending, then descending, then ascending again", () => {
  assert.deepEqual(toggledSort(null, "name"), { column: "name", direction: "asc" });
  assert.deepEqual(toggledSort({ column: "name", direction: "asc" }, "name"), { column: "name", direction: "desc" });
  assert.deepEqual(toggledSort({ column: "name", direction: "desc" }, "name"), { column: "name", direction: "asc" });
  assert.deepEqual(toggledSort({ column: "name", direction: "desc" }, "state"), { column: "state", direction: "asc" });
});

test("Filters match one of the values of a facet and every facet at once, and every word of the search", () => {
  assert.deepEqual(filterRows(rows, [state, label], { state: ["online"] }, "", row => row.name).map(row => row.name), ["node-10", "Mac"]);
  assert.deepEqual(filterRows(rows, [state, label], { state: ["online", "offline"], label: ["home"] }, "", row => row.name).map(row => row.name), ["Mac"]);
  assert.deepEqual(filterRows(rows, [state, label], {}, "NODE 1", row => `${row.name} ${row.state}`).map(row => row.name), ["node-10"]);
  assert.deepEqual(filterRows(rows, [state], { state: [] }, "  ", row => row.name).length, 3, "an empty facet and an empty search keep every row");
});

test("A facet counts the rows of each value, and its choices toggle", () => {
  assert.deepEqual([...facetCounts(rows, label)], [["gpu", 2], ["home", 1]]);
  const chosen = toggledFacet(toggledFacet({}, "state", "online"), "label", "gpu");
  assert.deepEqual(chosen, { state: ["online"], label: ["gpu"] });
  assert.equal(chosenCount(chosen), 2);
  assert.deepEqual(toggledFacet(chosen, "state", "online").state, []);
});
