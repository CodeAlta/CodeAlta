# Comment threads in the Monaco diff editor

What Monaco 0.57 does with view zones, folds and mouse targets in the diff of the Changes tab, measured in headless Edge before the review comment layer relies on it. The regression tests are `src/CodeAlta/frontend/src/monaco/diffComments.browser.test.ts`, with the fixture `diffComments.mount.tsx`: a 400-line file where old lines 50–52 are changed, old 100–104 removed and three lines added after old 300, shown by the real `DiffEditor` with `hideUnchangedRegions`.

## The opt-in API

`monaco/DiffEditor.tsx` leaves the diff as it was unless a host asks:

| Addition | What it does |
| --- | --- |
| `comments?: boolean` | `glyphMargin` on the diff. Monaco gives the old side its own glyph margin side by side whatever the option says (none in one column), so the option adds the margin of the new side. |
| `onEditors?: (editors: DiffEditors \| null) => void` | `{ original, modified, diff }` once created, `null` when disposed. |
| `onInline?: (inline: boolean) => void` | Whether the diff shows one column: reported once at creation and on each change, whether the cause is `sideBySide`, the 780 px breakpoint or a resize. |
| `DiffEditorHandle.reveal(side: "old" \| "new", line)` | Moves the caret to the line, unfolds its hidden region and the line after it, and centers the line. While a comparison runs (a new document, or a text that was read again) it waits for the result. |
| `removedLineAt(diff, mouseEvent)` | In one column, the old line under the pointer on removed lines. `null` elsewhere. |

`monaco/CodeEditor.tsx` has `comments?: boolean`, which turns on `glyphMargin` (the standalone editor shows none by default), and `onEditor?: (editor | null) => void`, for added and deleted files that show one side only.

While `comments` is on, `DiffEditor` also works around a Monaco gap: see question 2.

## Findings

**1. View zones on one side of a diff shown side by side.** Monaco keeps the two sides in line. A 60 px zone on either side, under an unchanged line, a changed line or a removed line, gets 60 px of padding on the other side. The lines below stay at the same height on both sides, and `getContentHeight()` of both editors grows by 60, so `onContentHeight` includes it. No work is needed in the comment layer.

**2. Folded regions (`hideUnchangedRegions`).** A zone under a line inside a folded region gets height 0 and is hidden. A zone shows only when the line after its own line is visible too.
- There is no public unfold API. Monaco unfolds when the caret enters a folded region: `setPosition` on either editor unfolds from the closer end of the region. When the top is closer, the line shows but the line after it stays folded, and so does the zone. `reveal` therefore puts the caret on line + 1 and then on the line, which unfolds both sides of the zone, before `revealLineInCenter`. Turning `hideUnchangedRegions` off is not needed.
- **Monaco gap:** when the diff unfolds a region, it ignores the view-zone change that the unfolding causes. A zone that becomes visible then gets no padding on the other side, and the sides drift apart by the height of the zone. This happens with `reveal` and with the user's own "show more" clicks. With `comments` on, `DiffEditor` adds and removes an empty zone after each `onDidChangeHiddenAreas`, which makes Monaco compute the alignment again. A test fails without this.
- Monaco compares texts again 200 ms after a change. Its new folds would hide a line that was revealed before the result arrived, so `reveal` waits for `onDidUpdateDiff` after any content change. A test fails without this. It does not wait after an `ignoreWhitespace` toggle.

**3. One column (inline).** This mode applies when `renderSideBySide` is false or the diff is narrower than 780 px. `onInline` detects it from the `side-by-side` class of Monaco's root, read on the layout events of both editors and after `updateOptions`. Toggling the width and then the option reported `false, true, false, true, false`.
- Removed lines are a view zone of the new side, placed after the new line before them. A click there gives `GUTTER_VIEW_ZONE` (5) in the margin or `CONTENT_VIEW_ZONE` (8) on the text, with `detail.afterLineNumber`. It carries no old line number and no glyph-margin target. `removedLineAt` maps the click to the old line from the line changes and the row under the pointer, which is how Monaco places its own action button. When a removed line wraps, its rows no longer match lines, and the function returns `null`.
- The old side is a narrow strip (about 31 px) of old line numbers that scrolls with the new side. It has no glyph margin. `reveal("old", n)` works through this strip, for folded lines and for removed lines.
- A zone can go only before or after the whole block of removed lines, not between two of them: it uses the new-side `afterLineNumber` of the block, with `ordinal` < 10000 to go above it (right under the new line) or > 10000 to go below it. Monaco's own zones use 10000, and ties go in no order to rely on.
- A zone on the old side in one column is drawn in the strip and pushes the old line numbers away from the rows they name. Do not do it.

**4. Glyph margin of both sides.** Side by side, a click in the glyph margin gives `GUTTER_GLYPH_MARGIN` (2) with the line number of that side on both editors. This held for changed, unfolded and added lines. On the text, the target is `CONTENT_TEXT` (6). Mark lines with decorations (`glyphMarginClassName`) on each side's own editor.

**5. Text read again (`setValue`).**
- The decorations of the model whose text is replaced are dropped. Those of the other side stay.
- View zones are not dropped, but they stay at the view line where they were drawn. They do not follow their model line, and they do not move to the new line with that number. `layoutZone` on each zone, or removing and adding them again, puts them back under their line.
- A new document (`setModel`, another `documentKey`) is a new view, and all its zones are gone.

## Recommendations for the comment layer

- Keep threads in the store and redraw them from it: on `onEditors`, on each `onDidUpdateDiff` (that is, after every text that was read again: re-anchor, then add or lay out the zones and set the decorations again), and on each `onInline` change.
- **Side by side.** Put old-side threads in the original editor and new-side threads in the modified editor. Monaco keeps the sides in line. Give zones an `ordinal` below 10000, such as 0, so that a thread stays right under its line, above Monaco's own zones on the same line (the padding, the removed lines). This ordering was measured in one column only.
- **One column.** Draw only new-side threads, in the modified editor. A thread on a removed line goes in the Comments list with **Show side by side**, as the plan says. If a thread must show in the diff, it can go under the whole removed block (`afterLineNumber` of the block, `ordinal` > 10000). New comments on removed lines can come from `removedLineAt` on a `GUTTER_VIEW_ZONE` mouse-down. A hover "+" there needs an overlay of its own, because Monaco owns that margin.
- **Reveal** with `handle.reveal(side, line)` after selecting the file. It unfolds, waits for a pending comparison and keeps both sides in line. In one column, reveal old-side threads with `reveal("old", line)` as well.
- Turn `comments` on only in review mode, so the diff keeps today's layout elsewhere.
