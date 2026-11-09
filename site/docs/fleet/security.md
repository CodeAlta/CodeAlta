---
title: Fleet Security
---

# Fleet Security

How Fleet keeps your machines and your conversations safe. The [overview](readme.md#what-the-server-cannot-do) shows the big picture: your devices sign who may do what, your machines check it, and conversations stay between your device and the machine that runs them.

## What a server sees

| A server sees | A server never sees |
| --- | --- |
| Your machines, their names, tags, versions and whether they are online | The prompts, answers and tool calls of a session |
| The sessions of a linked space: their state, provider, model, times and counts (turns, tool calls, tokens) | The title of a session: it travels encrypted with a key of your computers |
| The statistics of your machines: numbers, providers, models and tools | The names of your projects and files: projects travel as keys only your computers can name |
| Who reached which machine, when, and how much data went through | What a relayed connection carries: it is encrypted from one computer to the other |
| Sealed copies of the secrets you give your machines | The value of a secret, in the server or in any backup of it |

A session made **Private**, and every session of a space you did not link, is never published ([Choose what a server sees](server.md#choose-what-a-server-sees)).

## Signed by your devices

What your machines follow is signed by the devices of the people who may decide it, and every machine checks the signature before it acts. A server keeps and forwards these documents; it cannot write one, change one, or bring back an older one.

| Signed | By | So the server alone cannot |
| --- | --- | --- |
| The list of devices | The owner's computers, when they approve a device | Add a computer or a machine, or bring back a revoked one |
| People, teams, roles, grants and machine owners | A signer ([Sign what machines follow](people-and-access.md#sign-what-machines-follow)) | Give anyone access to a machine or a session |
| The policy and its kill switch | A signer ([Policies and Audit](policies-and-audit.md)) | Lift a stop or loosen a rule |
| Automations | The computer of the person who placed them | Run or change an automation |
| Updates | A computer of the owner, with the hash of each package | Make a machine install anything |
| **Continue anyway** on a budget, and approvals | The computer of the person who decided | Start a run past a budget or an approval |
| The telemetry setting | The owner's computer | Send your telemetry elsewhere |

A change reaches the machines once a signer signs the next version: until then they keep following the version they verified.

## Your machines and computers

- **Remote sessions run code on the machine.** A paired computer can start agents there that use its tools, as you would sitting at it. Pair only your own computers, and revoke the ones you no longer use.
- **Keys are pinned.** Each computer has its own key. Pairing records the key of the other side, and every connection checks it: a machine that presents another key is refused. The six words let you check, at pairing, that the link comes from the machine you think. A pairing both ways records each key on the other side after that one check, and each side revokes the other on its own. Through a server, the key comes from the list of devices your own computers signed, never from the server alone, and a device revoked there is refused.
- **Its sessions bypass permissions unless you choose otherwise.** A machine runs agents while nobody sits at it, so a session without a mode of its own runs commands and changes files without asking. Choose **Ask first** for the machine ([What its sessions may do](sessions.md#what-its-sessions-may-do)): a request then waits for a person, who answers it from a paired computer.
- **Its sessions are sandboxed where the sandbox can run.** Whatever their mode, they reach the package registries and the git hosts, never the home folder or the credentials of the machine. Where it cannot run, the page of the machine says what to install.
- **A personal plan serves its owner only.** On a machine shared with other people, a session someone else starts there runs with account credentials only (an API key, a gateway): the subscription its owner signed in on it (Codex, Claude Code, Copilot), or a provider whose credentials CodeAlta cannot tell (Codex CLI, an ACP agent), is refused to it, to its sub-sessions and to its fallbacks, and the start dialog lists only the providers that fit.
- **Agents never get more on another machine.** A session an agent starts elsewhere has at most the space, tools, permission mode and sandbox of the session that asked ([Let agents start sessions on it](sessions.md#let-agents-start-sessions-on-it)).

## Networks

- **Choose where the node listens.** By default it listens on the machine itself only. `--listen-all` opens it to every network the machine is on: prefer a private network you control.
- **Tailscale is the simplest safe network.** With [Tailscale](https://tailscale.com) on your computers, bind the node to the machine's Tailscale address (`altanode run --bind 100.x.y.z`) or use `--listen-all`: your computers reach it from anywhere, through an encrypted network only they are on, and nothing is exposed to the internet. Do not forward the node's port on your router.
- **With a server, machines need no open port.** Each one connects out to the server, which relays sessions it cannot read ([Reach machines through the server](server.md#reach-machines-through-the-server)).
- **A server encrypts its connections.** Put it behind an HTTPS address (Tailscale Serve, Caddy, your platform), or let it use a certificate of its own, which every device checks. Plain HTTP beyond the machine itself is refused unless you force it on a network you trust ([Run a server as a service](server.md#run-a-server-as-a-service)).

## Secrets and credentials

Each machine signs in to its providers itself; nothing is copied from your desktop. CodeAlta Desktop starts the sign-ins from the page of the machine ([Sign in on a machine](machines.md#sign-in-on-a-machine)), but the machine exchanges the code and keeps the token: the desktop sees a page and a code, never a token. The keys and tokens a machine needs are its own [secrets](../secrets.md#on-a-machine-with-altanode), set on it with `altanode secrets`, or values your computer seals for it through a server, which the server cannot open ([Secrets of your machines with a server](../secrets.md#secrets-of-your-machines-with-a-server)). Outputs and journals mask the values they meet.

## Sign-in

- **Signing in does not let a computer in by itself.** A new computer gets the keys that open your titles only once one of your computers approved it, after you compared its six words.
- **A new way to sign in needs one you already have.** Linking a passkey or an account asks you to sign in again with a way you have, to approve it on another of your computers, or, on your only computer, to use your recovery key ([Sign-in methods](server.md#sign-in-methods)).
- **A new computer or recovery key needs one too.** Approving a computer that joined with an enrollment code, or making a recovery key, asks you to sign in again with a way you have: the key of one computer alone, if it is stolen, adds no other that would approve what it asks.
- **Attempts are limited.** A server refuses for a while the sign-ins, enrollment codes, recoveries and setup codes that come too often from one address, and the recoveries in the name of one person: CodeAlta says when to try again.

## When something goes wrong

| Situation | What to do |
| --- | --- |
| Something must stop now | The [kill switch](policies-and-audit.md#the-kill-switch) stops the work of the fleet on every machine, or the ones you choose; machines keep it while the server is away |
| A computer is lost or stolen | Revoke it: on the page of each machine it was paired with, and, with a server, with the trash button of its row among the devices of the server's card ([Add machines and computers](server.md#add-machines-and-computers)). It is refused at once |
| A person leaves | Deprovision them in [People and Access](people-and-access.md#when-a-person-leaves), then sign the access document |
| Every computer of yours is lost | Recover with your [recovery key](server.md#keep-your-recovery-key) on a new computer |
| You no longer trust a server | Leave it ([Leave a server](server.md#leave-a-server)): what this computer published there is removed |
