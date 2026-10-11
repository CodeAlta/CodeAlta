import { Readable } from "node:stream";
import { spec } from "node:test/reporters";

// Keep Node's failure formatting, locations, diagnostics and final totals, without a line for every
// successful test. Drop starts too: spec pairs those with results to print the successful suite tree.
// Expected TODO failures are not failures of the run. Never filter stdout/stderr or runner errors.
export default async function* failuresReporter(source) {
  yield* Readable.from(source).filter(({ type, data }) =>
    type !== "test:start" && type !== "test:pass" && !(type === "test:fail" && data.todo)
  ).compose(new spec());
}
