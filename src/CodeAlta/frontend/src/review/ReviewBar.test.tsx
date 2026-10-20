import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { ReviewBar } from "./ReviewBar";
import { ReviewsListOpenerContext } from "./useReviews";

test("the review bar links to every review in flight only where the window can open them", () => {
  const bar = <ReviewBar title="plan.md" threads={[]} projectId="p1" />;
  assert.doesNotMatch(renderToStaticMarkup(bar), /aria-label="All reviews"/);
  const opened: (string | null)[] = [];
  const linked = renderToStaticMarkup(<ReviewsListOpenerContext.Provider value={projectId => opened.push(projectId)}>{bar}</ReviewsListOpenerContext.Provider>);
  assert.match(linked, /aria-label="All reviews"/);
  assert.match(linked, /title="Every review in flight, in Work items"/);
});
