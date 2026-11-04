---
title: Audit viewer and exports, signed policies and the kill switch (M4, 4.0)
status: in-progress
created: 2026-11-04
summary: The audit log as a filterable, paged timeline with chain verification and CSV / JSON Lines exports; the organization's policy as a signed, hash-chained document that machines pin, verify and enforce; the kill switch as a signed stop in that document that machines obey offline.
---

# Audit viewer and exports, signed policies and the kill switch

The spec is [security and privacy](../../doc/fleet/security-and-privacy.md) (policies and the kill switch, audit log),
[access control](../../doc/fleet/access-control.md) (audit, budgets and policies), [observability](../../doc/fleet/observability.md)
(audit is not telemetry) and [console](../../doc/fleet/console.md) (Policies and budgets, Audit). Each slice is a commit with
its tests. The protocol only gains: optional query parameters of `GET /api/v1/audit`, `GET /api/v1/audit/export`,
`/api/v1/policies`, a control notification `policy.changed`, optional fields. One SQLite migration: `0017_policies.sql`.

## Decisions taken here

- **Audit**: filters (action or its prefix, actor person, actor device, result, success or failure, target, address, a
  time range, words) and both orders, cursors by sequence; exports of the filtered range in CSV and JSON Lines, every
  record with its sequence and hashes, so an export of the whole log verifies elsewhere; an export needs `audit.export`
  and is itself audited (`audit.export`). Verification of the chain answers its head (sequence and hash) as well.
- **The policy document** (`codealta-policy/1`) is a chain like the access document: each version names the hash of the
  one before, the entry of the device list and the version of the access document its signer read. It is signed by a
  controller device of a signer of the verified access document (an owner, or an admin who signs; before any access
  document, the person who set the server up). The server checks and relays; machines pin the chain per server, keep
  the last version they verified on disk, and apply it while the server is unreachable, across restarts. A version
  that does not verify is refused and changes nothing.
- **Signed only.** The spec lets a node take a stricter rule unsigned; here every rule and every stop is signed (the
  owner's requirement for 4.0): a server can withhold a version (deny service), never forge or replay one.
- **Rules, exactly the spec's list**: allowed providers and models, minimum sandbox, tools turned off, network egress,
  who may start where (people and agents starting work on other machines), maximum session length, required approval
  modes (the widest permission mode), what must be published (the facts of the organization's sessions), plus account
  credentials for every automation (orchestration). A rule absent says nothing. Budgets stay their own signed documents
  ([observability](../../doc/fleet/observability.md#budgets-and-alerts)), which machines already enforce: the policy
  does not repeat them. The managed value of secrets (secrets) waits for organization secrets on machines.
- **Where rules apply**: the work of the fleet on a machine (sessions started from another device, agents of other
  machines, automations the server gives), in the spaces linked to the server: a policy narrows, never widens, and
  never reaches personal spaces or what a person does at the machine's own keyboard.
- **The kill switch is a stop in the policy document**: everything, chosen machines, or the machines of chosen teams.
  Signing it is one action; lifting it is the next version. A machine it names stops the work of the fleet (remote and
  agent sessions, automation runs) and refuses new work until a version lifts it; local work goes on. The desktop shows
  a banner while it is on.

## Slices

- [x] 1. Audit on the server: filters, orders, cursors, exports (CSV, JSON Lines), verification head; tests.
- [x] 2. Audit in the client, the desktop RPC (pages, check, export to a file the person chooses) and `alta fleet audit`
      (list, verify; no export: the host writes no file for a session); tests.
- [ ] 3. The audit viewer in CodeAlta Desktop (Settings > Fleet > Audit): timeline, filters, paging, verification, exports.
- [ ] 4. The policy document: format, rules, stop, chain verifier, describe; tests.
- [ ] 5. Policies on the server: migration 0017, endpoints, `policy.changed`, audit; tests.
- [ ] 6. Policies on machines: anchored chain kept on disk, refresh on connection and notification, the stop and the rules
      enforced on nodes and desktops; tests (stop offline, unverifiable version refused, lift).
- [ ] 7. Policies and the kill switch in CodeAlta Desktop (Settings > Fleet > Policies): rules, versions, sign; Stop
      with its popover; the banner; `alta fleet policy` (read only).
- [ ] 8. User guide, `doc/fleet`, the `codealta-fleet` skill.
