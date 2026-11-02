# Collaboration

How people work together around agents in a fleet: who is around, what they hand to each other, what they share and
how they talk about a project. It is not a chat application: it lives beside projects and sessions, and links out to
the tools teams already use.

## Principles

- **Around the work.** Everything attaches to a project or a session: a message is in a project's channel, a share is
  a session, a handoff is a session given to someone. There is no free-floating chat.
- **Sharing is an act.** Nothing a person writes to an agent reaches anyone else, or the server, until that person
  shares it. What is shared is shown as shared, and can be revoked.
- **Agents are participants, never impostors.** An agent can post in a channel (an automation's result, a session that
  ended), always marked as an agent of a named session and person. The person stays accountable for what their
  sessions do: the agent is their delegate, never the assignee (Linear's model, the clearest in the field).
- **Humans stay in the loop where it counts.** Questions and approvals reach the right person on the device they are
  using, and only people allowed to answer can.
- **Small and fast.** Quick messages, cards, mentions; no threads of threads, no reactions zoo. Teams keep Slack or
  Teams for the rest, and the fleet can post there.

## People and presence

- A person has a profile: name, avatar (from the sign-in provider, an upload, Gravatar, or initials; see
  [identity](identity.md#profiles-and-avatars)), and the teams they belong to.
- **Presence** says who is online, on which machines, and, if they allow it, which project they are in. A person can be
  invisible; presence is never shown to someone who shares no project with them.
- A session's card shows the person it works for, its machine and its state.

## Project channels

Each project of an organization (a repository key, see [sessions across machines](sessions-across-machines.md#projects-across-machines))
has a channel, visible to the people of the teams that have the project.

- **Messages**: short Markdown, mentions of people (`@bob`) and teams, links. One level of replies.
- **Cards**: a session (with its live state for those who may see it), a handoff, a pull request, a commit, a CI run,
  an automation run, a plan or a task of `.alta/`. A card is a link with a live preview, not a copy.
- **Activity**: sessions started and ended in the project (by those who publish them), pull requests opened by
  sessions, the runs of the project's and team's automations with their owner and outcome
  ([orchestration](orchestration.md#outputs-and-reporting-back)), handoffs. Activity can be filtered out of the
  channel's view.
- **Offline**: a message written offline waits in the outbox and is sent on reconnection, in order.
- **Retention**: set by the organization (forever, or N days); a deleted message is gone from the server.
- **Where it shows in CodeAlta Desktop**: a **Channel** tab of the project (beside Work items and Issues), and a badge on
  the project in the Explorer for unread mentions.

The server stores channel messages, since they are written for others: readable by the organization in a Managed
space, sealed with the space key in a Private one (a household), where the server only relays and keeps them
([security](security-and-privacy.md#what-the-server-sees)).

## Sharing a session

| Kind | What the recipients get | Typical use |
| --- | --- | --- |
| **Snapshot** | A copy of the timeline up to now, uploaded to the server; updated when the owner says so | "Look at how I fixed it" |
| **Live** | The live session, read-only: timeline, live display, jobs' output | "Watch this run", pairing |
| **Co-drive** | Live, and they may send prompts, steer, stop and answer, per the owner's choice | Two people, one agent |
| **Fork for** | Their own fork, on their machine, under their identity | Handoff, "try your idea" |

- Recipients are people, teams, or the project's channel. A share has an expiry if the owner wants one, and can be
  revoked at once (a live share closes; a snapshot is deleted from the server).
- **Live and co-drive** go through the fleet's end-to-end channels from the owner's node to each viewer; the server
  relays without reading. A **snapshot** is stored by the server: sealed for its recipients in a Private space,
  readable by the organization in a Managed one. A **fork for** someone travels end to end to the recipient's node.
- A share gives the conversation, never the machine: no credential, file or terminal of the owner's node reaches the
  recipients (Amp's multiplayer threads expose their secrets to every participant; this design does not).
- **Authorship.** In a co-driven session, each prompt records the person who sent it (prompt provenance gains the
  person), so the timeline shows who said what, and the statistics count it to them.
- **What is shared** is the conversation and what the journal holds (tool calls, their output, file changes). The
  dialog warns when tool outputs may hold secrets (a `.env` read, a token in a log), and the owner can share **without
  tool output**.
- A person who leaves the organization loses access to what was shared with them; what they shared stays with the
  organization if its policy says so.

## Handoffs

A handoff gives unfinished work to someone else, with what they need to continue.

```mermaid
sequenceDiagram
    participant A as Alice (session Z)
    participant Ag as Agent of Z
    participant S as Server
    participant B as Bob
    A->>Ag: Hand off to Bob
    Ag-->>A: Handoff note: what was done, what is left, branch, pull request, how to test
    A->>S: Post the handoff card in the project's channel, share Z (live) with Bob
    S-->>B: Notification
    B->>S: Open the card, read Z live
    B->>B: Continue in my fork: Z' on Bob's machine, Bob's identity
    B->>S: Z' is linked to the handoff card; the channel shows its progress
```

- The **handoff note** is written by Z's own agent from its notes and its work, as a host prompt of CodeAlta; Alice
  edits it before posting.
- Bob can **continue in a fork** (the default: Alice's session stays hers), or, if Alice chose **give**, become the
  owner of Z itself (a move to Bob's node and a change of person, recorded in the journal).
- Bob's fork works on Alice's branch, or on a new branch from it; the card says which.

## Notifications

| Event | Who |
| --- | --- |
| A mention, a reply to me, a handoff to me | The person |
| A session of mine waits for an answer or an approval, failed, or ended (when I asked to be told) | The person |
| A share with me, a share revoked | The person |
| A shared session waits for an answer I may give (co-drive) | The co-drivers |
| A team machine is offline, a policy blocked a run | The admins who subscribe |

- **Routing follows presence**: the device the person is using now (a toast of CodeAlta Desktop, as today for a waiting
  session), else the phone (Web Push of the mobile page), else nothing until they come back; a digest later.
- Quiet hours and per-project mute. An approval is never answered from a notification by mistake: it opens the card,
  bound to the exact request ([runtime](../runtime.md#owned-session-commands)).

## Questions and approvals across people

- A session's question or approval goes to the person it works for, wherever they are (above).
- On a **team machine**, a policy can route some approvals to a team instead ("production deploys need a lead"): the
  card shows to the team, the first allowed answer wins, and the journal records who answered.
- An agent can ask a person other than its own: `alta ask --to @sam` posts a question card in the project's channel and
  waits; Sam's answer comes back as the next prompt. Only within the project's people, and never to approve a permission
  in another person's name.

## Activity and awareness

- **Who works on what**: the project's sessions at work, by person and branch (for those who publish them), so two
  people do not send agents to the same bug.
- **Overlap warning** (later): when two published sessions change the same files on different branches, both owners
  are told. It needs the paths of changed files at the server, so it is opt-in per organization.
- **Digest** (later): what happened in a project or the fleet since you last looked, written by an agent from the
  activity, on demand or every morning.

## Integrations

- **Git hosts**: cards for pull requests, commits and CI runs come from the trackers CodeAlta already reads (the Git
  plugin, `alta issue`).
- **Slack and Teams** (later): an organization can mirror a project's channel activity, or only handoffs, to a webhook;
  mentions do not round-trip.
- **`.alta/` work items**: a task or a plan can be posted as a card, and a handoff can carry the plan the session
  executes.
