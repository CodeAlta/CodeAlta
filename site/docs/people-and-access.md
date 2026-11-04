---
title: People and Access
---

# People and Access

A [CodeAlta Server](fleet.md#connect-to-a-codealta-server) knows the people of your household or company, what each may do, and who owns each machine and automation. In CodeAlta Desktop, open **Settings** > **Fleet** > **People and access**.

At home, the page shows your people and your machines: everything is yours, and the people you invite see what you share with them. Teams, grants and service identities appear when your organization uses them, or with **Show teams, grants and service identities**.

## Invite a person

1. Click **Invite a person…**, give their name and a role (**Member** by default), and click **Invite**.
2. Give them the code and the address of the server, each with **Copy**. The code works once, for three days.
3. On their computer, they open **Settings** > **Fleet** > **Server**, click **Join an organization…**, give the address and choose **Use an enrollment code instead**.
4. Their computer shows six words. Approve it on yours when it shows the same words (see [what waits for you](fleet.md#add-machines-and-computers)).

Until then they are listed as **Invited**. People who sign in with your company's sign-in join without an invitation, as members.

## Roles

| Role | May |
| --- | --- |
| **Owner** | Everything: people, roles, teams, sign-in, machines, budgets, the audit log |
| **Admin** | People, teams, machines, budgets and the audit log; only an owner makes owners and admins |
| **Member** | Their own machines, sessions and automations, and those of their teams |
| **Auditor** | Reads the audit log, statistics and who can do what; changes nothing |
| **Guest** | Only what is shared with them |

Change a role with the menu on the row of the person. With your company's sign-in, **Roles from groups** can give them instead, by the id of each group (see [Fleet](fleet.md)). No role reads what others did not share: an owner or an admin sees that a session exists and what it costs, never what it says. The organization always keeps an owner.

## Teams and team machines

Click **New team…** and add its members. A team has **maintainers**, who run it, and **members**, who use it. A team with members keeps a maintainer.

- **Team machines**: on the row of a machine, click **Give to…** and choose the team; a maintainer accepts it. Each member of the team then starts sessions on it and sees only their own sessions there; its maintainers manage it.
- **Its machines run the automations of its members**: off by default. Its maintainers' automations run there either way.
- **Dissolve** gives what the team owned to the organization.

## People on your own machines

Your machines are yours: the people of your organization see and use them only when you let them. When someone tries, their first try becomes a request: the Fleet button tells you, and the page of the machine (**Settings** > **Fleet** > **Machines**, then the machine) shows it under **People**, with **Let in** and **Refuse**. **Add a person…** lets someone in before they ask, and the switches beside each name set what they may do: view, drive and start sessions. A person you let in sees and drives only the sessions they started there, never yours. The machine keeps this list itself: the server cannot add anyone to it.

## Owners and transfers

Every machine and automation has one owner: a person, a team or the organization. **Machines and automations** lists them; **Give to…** offers one to another owner, who accepts or declines it under **Waiting for you**. An automation that changes owner is **Paused** until its new owner clicks **Resume**.

- Given to a person from your computer, an automation goes with what it does, sealed for their computers only. **Resume** on their computer adds it to their automations there (add its project to CodeAlta first), and it leaves your computer and your machines.
- Given to a team or the organization, or left by a person who left, it keeps only its facts: what it does stays sealed for its former owner, so its new owner makes it again.

## Grants

A grant gives a person or a team one capability somewhere: on the organization, a team, or one machine. **Add a grant…** chooses who, what and where; the trash icon takes it back.

- Reading content (a session, the log of a machine) is granted on one machine or session, never on a whole team or the organization. **Read a session** lists your own sessions whose title your computer opens.
- The server cannot read what a session says. Check **The content is sealed from the server** and the grant shows **Waiting for keys**; on a computer of yours, **Confirm keys** seals the session for the grantee's computers only. The session and its title then appear in the grantee's **Fleet** page, until the grant is taken back.

**Service identities** are what team and organization automations run as. A new one stays off until a person turns it on.

### Automations of a team

When an automation runs on a machine of a team you maintain, **Publish to** in its editor makes it an automation of that team: the team owns it, every maintainer reads, changes and runs it. On a machine of the organization, owners and admins publish it to the organization the same way. **Me** keeps it yours.

- It runs only while the team has a service identity turned on, once that change is signed (see [Sign what machines follow](#sign-what-machines-follow)).
- A machine of a team runs automations with account credentials only: an API key or a gateway, never a personal plan such as a Claude, Codex or Copilot subscription.

A machine given to the organization runs the automations of the organization only. They run as a service identity of the organization, with account credentials only.

## Sign what machines follow

Machines never take teams, team machines or suspensions from the server alone: they follow them once an owner or an admin signs them. When something changed, **Waiting for you** lists it under **Changes machines do not follow yet**. Click **Review and sign…**: your computer reads the changes itself, lists them, and signs them with its key when you click **Sign**. A change made by a maintainer who is not an admin waits there too.

## When a person leaves

On their row, click the trash icon, choose who takes what they owned (the organization, a team or a person), and click **Remove from the organization**:

- they are suspended at once, and the server refuses their devices;
- your computer signs their devices out of the list of devices;
- what they owned goes to the one you chose, paused until resumed; resources whose owner left are listed under **Waiting for you**;
- once you sign, team machines stop their sessions and refuse the automations they gave.

## Agents and access

Agents read people and access with `alta fleet access show`, `people`, `teams`, `grants`, `owners`, `transfers` and `review`. They never change access: `alta fleet access propose` records a change, which appears under **Waiting for you** with **Apply** and **Dismiss**.

## Access review

Owners, admins and auditors click **Copy who can do what (CSV)** to copy every role, team role, grant and owner, for a review. Every change of access is in the audit log of the server, refused attempts included.
