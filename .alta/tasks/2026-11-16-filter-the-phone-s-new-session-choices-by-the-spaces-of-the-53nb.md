---
title: "Filter the phone's new-session choices by the spaces of the Mobile plugin"
kind: gap
status: pending
created: 2026-11-16
summary: "The phone page lists agent prompts, providers and models of a machine without the space filter of the Mobile plugin, on this computer and on other machines; found by the review of #456."
---

## Why
`MobileApi.OptionsAsync` (`src/CodeAlta.Plugin.Mobile/MobileApi.cs`, around line 86) runs `prompt list`, `provider list`, `model list` and `model resolve` with no session and no space. `alta prompt list` filters by the space of the calling session (`CallerSpaceFilterAsync` in `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs`), and a plugin call has none, so a phone limited to the Personal space sees the prompts and providers reserved to Work. For another machine the same reads go by `sessions.read` (`FleetSessionReads`: `prompt.list`, `provider.list`, `model.list`), which carry no space either. Sessions, asks, images and notifications are filtered; the choices are not. The guide (`site/docs/plugins/mobile.md`, Spaces) now says so.

## What to do
- Give the machine-level reads a space (`--space`, checked against the assigned spaces of the plugin) and filter prompts, providers and models with the resource rules of that space, locally and through `sessions.read`.
- The new-session form of the phone already knows the space it creates in: read the choices for that space.
- Tests: a phone limited to one space does not list a prompt or a provider of another, on this computer and on another machine.

## Where
`src/CodeAlta.Plugin.Mobile/MobileApi.cs`, `src/CodeAlta.Fleet/NodeApi/FleetSessionReads.cs`, `src/CodeAlta.Fleet/NodeApi/FleetNodeService.Reads.cs`, `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs`, `src/CodeAlta/frontend/src/mobile/app.ts`.
