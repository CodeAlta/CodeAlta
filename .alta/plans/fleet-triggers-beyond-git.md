---
status: done
---

# Triggers beyond Git (fleet 4.0, M4)

Spec: `doc/fleet/orchestration.md` ("Triggers", "Placement and dispatch", "Outputs and reporting back", "Try before
enabling"), `doc/fleet/secrets.md`, `doc/fleet/protocol.md` (REST `hooks`), `doc/fleet/security-and-privacy.md` rule 7.

## Where each trigger runs

| Trigger | Evaluated by | Authenticity | Reply |
| --- | --- | --- | --- |
| `jira_version` (a version of a Jira project is released) | The machine that runs the automation, polling Jira REST with its own token | Polled from Jira with the machine's credentials | None (a version has no thread) |
| `jira_query` (an issue starts to match a JQL query, or changes while it matches) | Same | Same | A comment on the issue |
| `slack` (a message in a channel, or a mention of the app) | The machine, over Slack Socket Mode (app-level token) | Socket Mode connection the machine opened to Slack | A reply in the message's thread |
| `incident_io` (an incident event pushed by incident.io) | The server's ingress (`POST /api/v1/hooks/{id}`) receives it; the machine checks the Standard Webhooks signature with a secret the server does not hold, then re-fetches the incident by id | Signed by the source + re-fetched | A custom item on the incident's timeline |
| `webhook` (any tool that pushes) | The server's ingress, forwarded raw; the machine checks the signature (`standard`, `github`, `hmac_sha256`) | Signed by the source | None |

- Credentials are `${secret:NAME}` references to the secrets store (host-only: never the agent's shell); a literal value
  is refused. Sites, emails, channels may be `${var:NAME}` or plain text.
- Only fields the signed definition binds reach the prompt (fixed per source; `fields` JSON pointers for `webhook`),
  each one line, marked as someone else's text.
- Each event has an id; the run claims `event:<trigger key>#<event id>` (locally and with the server): it runs once.
- Hook events wait at the server for their machine (24 h), then are dropped; the body is deleted once acknowledged.
  Delivery is at least once (a resend on connect may cross a push); the machine takes an event once (its id, claims).
- Agents (`alta automation create`) may write these triggers, but such an automation is saved disabled and only a
  person enables it (the Automations tab).

## Slices

- [x] 0. Plan (this file).
- [x] 1. Model and configuration: the five trigger kinds, their fields (`site`, `email`, `token`, `jira_project`, `jql`,
  `channel`, `match`, `app_token`, `bot_token`, `api_key`, `hook`, `secret`, `signature`, `fields`, `reply`), validation
  (secret references only), TOML read/write, keys; desktop RPC items; `alta automation` trigger text; agents' rule.
- [x] 2. Integration clients in `CodeAlta.Automations/Integrations`: Jira REST, Slack Web API + Socket Mode,
  incident.io API, webhook signature schemes; tests against in-process fakes (HTTP handler fakes, a loopback WebSocket).
- [x] 3. Engine: polling of the Jira triggers, Slack Socket Mode listeners, inbound hook events, dedupe and claims,
  prompts with bound fields, replies to the source after the run (kept, not posted, in a try), run records with the reply.
- [x] 4. Protocol + server: `hooks` REST (`GET/POST /api/v1/hooks`, `DELETE /api/v1/hooks/{id}`, `GET .../{id}/events`,
  anonymous `POST /api/v1/hooks/{id}` ingress), migration `0015_hooks.sql` (renumbered at the merge after `0014_access.sql`), control `hooks.event` / `hooks.ack`,
  pending events sent on connect, expiry; tests.
- [x] 5. Node: hook events from the connector to the engine, acknowledgements, claims of events with the server; tests.
- [x] 6. Desktop: RPC `testTrigger`, `sampleEvent`, `tryRun`, hook creation through the server for the machine; the
  editor's rows and setup guide for each trigger (fields, secrets, test connection, sample event, Try); frontend tests.
- [x] 7. `alta fleet hook list` (read only; a person makes a hook in the editor), the `codealta-fleet` skill, `site/docs` (automations, fleet).
- [x] 8. Verification: build, .NET suites touched, `npm test`, `lunet build`; plan done.

## Not in this work

- Graduated autonomy (allowed tools replaced by fakes in a dry run), team/organization scopes, PagerDuty, fleet events,
  the fleet simulator: other parts of M4.
