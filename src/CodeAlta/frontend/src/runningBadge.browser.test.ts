import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ActivitySpinner } from "./ActivitySpinner";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The badge of a project or a label where sessions run, at the end of the row of that scope, with the classes
// RunningSessionBadge gives it (its store cannot be rendered on a server).
function badge(running: number) {
  const spinner = renderToStaticMarkup(createElement(ActivitySpinner, { size: 12, label: `${running} sessions running` }));
  return running > 1 ? `<span class="session-running" data-count="${running}">${spinner}<span class="session-running-count" aria-hidden="true">${running}</span></span>`
    : `<span class="session-running">${spinner}</span>`;
}

// In a row of a project of the Explorer, with the styles of the window: the count of the sessions that run is on the
// right of the spinner, on its line, as the counts of the other marks are, and the row keeps its height.
test("the count of the sessions that run is beside the spinner, and the row of its project keeps its height", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  await withSessionWindow({}, async ({ evaluate, until, exceptions }) => {
    const row = `document.querySelector('.project-list .project-action-row > button')`;
    await until(`!!${row}`, "the row of a project in the Explorer");
    const measure = (markup: string) => evaluate(`(() => {
      const button = ${row}, holder = document.createElement('span');
      holder.innerHTML = ${JSON.stringify(markup)};
      const mark = holder.firstElementChild;
      button.append(mark);
      const height = button.closest('.project-action-row').getBoundingClientRect().height, spinner = mark.querySelector('.activity-spinner').getBoundingClientRect();
      const count = mark.querySelector('.session-running-count')?.getBoundingClientRect();
      const result = { row: height, spinner: [spinner.left, spinner.top, spinner.right, spinner.bottom], count: count ? [count.left, count.top, count.right, count.bottom] : null };
      mark.remove();
      return result;
    })()`) as Promise<{ row: number; spinner: number[]; count: number[] | null }>;

    const one = await measure(badge(1));
    const two = await measure(badge(2));
    assert.equal(one.count, null, "one session running shows the spinner alone");
    assert.ok(two.count, "two sessions running show their count");
    const [spinnerLeft, spinnerTop, spinnerRight, spinnerBottom] = two.spinner, [countLeft, countTop, , countBottom] = two.count;
    assert.ok(countLeft >= spinnerRight - 0.5 && countLeft > spinnerLeft, `the count is on the right of the spinner: ${JSON.stringify(two)}`);
    assert.ok(countTop < spinnerBottom && countBottom > spinnerTop, `the count is on the line of the spinner: ${JSON.stringify(two)}`);
    assert.equal(two.row, one.row, "the row is no taller with the count");
    assert.deepEqual(exceptions, []);
  });
});
