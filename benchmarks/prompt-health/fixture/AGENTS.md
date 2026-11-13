# shopkit

Small order utilities for the shop back office. Plain Node.js (ES modules), no dependencies.

## Commands
- Test: `node --test` from the root.

## Conventions
- Amounts are integers in cents. Convert at the edges with `toCents` of `src/money.js`.
- `src/vendor/` is third-party code, replaced on upgrade: never edit it.
- One test file per module, under `test/`.

## Notes
- Note: reports are generated at night by the scheduler, not by this package.
- Note: CSV files come from the warehouse export and use `,` as separator.
