# Skills dialog enablements

- Status: Approved
- Completed: 2026-06-03
- Plan file: `.alta/plans/2026-06-03-skills-dialog-enablements.md`
- Created: 2026-06-03
- Task: Add global/project skill enablement controls and compact bulk actions to the skills dialog for issue #35.
- Git: `.alta/plans/` is not ignored by `.gitignore`; commit this plan with the related implementation work. Current worktree also has unrelated untracked `.alta/config.toml`, `.alta/mcp.json`, and `site/img/alta-plan-mode.png`.

## Objective
- Let users disable/re-enable discovered skills globally and for the selected project, persisted in the corresponding `.alta/config.toml` files.
- Make the skills dialog less bulky by surfacing per-skill checkboxes and concise bulk actions in the left/sidebar area.
- Keep skill authoring, related-file opening, validation, shadowing, and manual activation behavior intact except that disabled skills are not advertised to models and should not be activated.
- Non-goal: redesign the broader workspace UI, add new dependencies, or implement provider-specific skill behavior.

## Context and evidence
- `src/CodeAlta/Views/SkillsManagementDialog.cs` currently has a top toolbar (`Scope`, `Filter`, `New skill`, `Activate`, `Open`, `Refresh`), a verbose intro/summary, and a left `ListBox<SkillRow>` rendered as text (`SkillRow.ToString()` at lines 708-714).
- `src/CodeAlta/App/SkillsManagementService.cs` loads descriptors via `SkillCatalog.ListAsync` with all invalid/shadowed/untrusted entries included for management; it does not read or write config enablement today.
- `src/CodeAlta.Catalog/CodeAltaConfigDocument.cs` currently has `Chat`, `Providers`, and `Plugins`; plugin enablement is already persisted in global/project `.alta/config.toml` through `CodeAltaConfigStore.SaveGlobalPluginEnabled` / `SaveProjectPluginEnabled`.
- `src/CodeAlta.Catalog/Skills/SkillCatalog.cs` computes `IsModelVisible` from validity, shadowing, trust, and metadata only; no disabled/enabled state exists.
- Runtime model advertisements use `SkillCatalogQuery.ModelVisibleOnly = true` in `src/CodeAlta.Orchestration/Runtime/AgentInstructionTemplateProvider.cs`; runtime activation builds its own skill query in `SessionRuntimeService.BuildSkillCatalogQuery`.
- Live-tool `alta skill list/show/activate` builds skill queries in `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs`.
- Existing tests for skill discovery live in `src/CodeAlta.Catalog.Tests/SkillCatalogTests.cs`; UI-adjacent service tests live in `src/CodeAlta.Tests/SkillsManagementServiceTests.cs`; docs for skill behavior live in `doc/skills.md` and `site/docs/workspace.md`.

## Assumptions and open decisions
- Use normalized skill names as the persisted enablement key because shadowing and activation already treat skill name as the identity.
- Recommended config shape is a compact disabled list per config file, e.g. `[skills] disabled = ["ilspy-decompile"]`; missing/empty means all skills are enabled for that config scope.
- Effective enablement should be an AND of global and project gates: a skill is available only when it is not disabled globally and not disabled in the selected project. This makes global disable authoritative and project disable additive, avoiding tri-state/inheritance UI complexity.
- Bulk actions operate on the currently loaded skill set, preferably the current filtered list when a filter is active; document this in tooltip/status text. If the user expects future/new skills to be disabled by a master default, that would require a larger schema (`default_enabled`) and should be a follow-up.

## Design notes
- Add skill enablement config to `CodeAltaConfigDocument` and `CodeAltaConfigStore`:
  - `CodeAltaSkillSettingsDocument` with `IReadOnlyList<string>? Disabled` (or a serializable `List<string>? Disabled`).
  - load helpers for global/project disabled sets and save helpers that normalize, trim, de-dupe, sort, and prune empty/default skill config.
  - preserve existing TOML serializer and validation patterns; no custom parser unless Tomlyn cannot serialize the chosen shape cleanly.
- Thread enablement through discovery with minimal API churn:
  - Add a query/context property such as `SkillCatalogQuery.DisabledSkillNames` or `SkillDiscoveryContext.DisabledSkillNames`.
  - Add `SkillDescriptor.IsEnabled` and possibly `SkillDescriptor.DisabledBy`/`DisablementReason` for UI details.
  - Compute `IsModelVisible = existing conditions && IsEnabled` and make activation refuse disabled descriptors even when `IncludeInvalid/IncludeShadowed` are true.
- Update all query builders that should respect config:
  - `SkillsManagementService.LoadAsync` should include disabled descriptors for management display and enrich rows with global/project/effective states.
  - `AgentInstructionTemplateProvider` and `SessionRuntimeService` should read effective disabled skill names from global/project config before advertising or activating skills.
  - `BuiltInAltaCommandContributor` should apply the same disabled sets for `alta skill list/show/activate` so live tool behavior matches UI/runtime.
- Compact UI recommendation:
  - Keep one scope selector and one filter field, but shorten the intro/status copy and move explanatory text into tooltips/status messages.
  - Replace the text-only skills list with a table-like list row: `G` checkbox, `P` checkbox (disabled/hidden with no selected project), status icon/name/source. Use tooltips: `G = global ~/.alta/config.toml`, `P = project <project>/.alta/config.toml`.
  - A checkbox checked means that scope does not disable the skill; unchecked means this scope disables it. Effective disabled rows should be visually muted and show `disabled` in status/detail.
  - Add a compact bulk action control near the list header: `Scope: Global | Project | Both` plus `Enable all`, `Disable all`, `Invert`. This is clearer than making each action button imply a hidden scope.
  - Preserve existing `Activate`, `Open SKILL.md`, `Open related`, `New skill`, and `Refresh` actions, but disable/warn on activation for disabled skills.

## Risks and challenges
- Activation currently resolves descriptors with invalid/shadowed/untrusted included; implementation must explicitly prevent disabled activation in catalog/runtime/live-tool paths, not only in the UI.
- Project checkbox behavior must be clear when no project is selected; do not create a project config unless a project is selected and the user changes project-scope settings.
- Disabled lists by skill name affect all sources with the same normalized name; this aligns with existing shadowing but should be visible in details.
- Bulk actions may touch many entries in user/project config; status text should name the target config scope and count changed skills.
- TOML serialization may reorder config sections; keep `CodeAltaConfigStore` changes consistent with existing normalization and avoid broad formatting churn beyond current serializer behavior.

## Implementation checklist
- [x] Add `Skills` config models to `src/CodeAlta.Catalog/CodeAltaConfigDocument.cs` with XML docs and JSON/TOML naming consistent with existing config models.
- [x] Extend `src/CodeAlta.Catalog/CodeAltaConfigStore.cs` with load/save helpers for global and project disabled skill sets, normalizing skill names case-insensitively, pruning empty skill config, and validating no invalid entries are persisted.
- [x] Update config normalization/validation and `CodeAltaTomlSerializerContext` usage as needed so `[skills] disabled = [...]` round-trips and validates.
- [x] Extend `SkillCatalogQuery`/`SkillDiscoveryContext` and `SkillDescriptor` to carry enablement, and update `SkillCatalog.CreateDescriptor`, shadowing, filtering, and activation to set/use `IsEnabled` and exclude disabled skills from `IsModelVisible`.
- [x] Update `SkillsManagementService` to load global/project disabled sets, expose row/config state to the dialog, and provide methods to set or bulk-update global/project skill enablement.
- [x] Update `AgentInstructionTemplateProvider`, `SessionRuntimeService.BuildSkillCatalogQuery`, and `BuiltInAltaCommandContributor.BuildSkillQueryAsync` to pass effective disabled skill names from config for the active project/context.
- [x] Refactor `SkillsManagementDialog` left pane into a compact checkbox table with `G`/`P` columns, bulk scope selector, bulk action buttons, clear status text, and disabled activation affordances.
- [x] Update details/summary text to include enabled/effective disabled state and the config source(s) causing disablement.
- [x] Update docs in `doc/skills.md`, `doc/catalog-and-config.md`, and `site/docs/workspace.md` to describe skill enablement config and UI controls; update other docs only if touched behavior requires it.

## Verification checklist
- [x] Add/adjust catalog tests proving config disabled lists normalize, round-trip, and prune empty/default skill config.
- [x] Add `SkillCatalogTests` covering disabled skills: still discoverable when management asks for them, not model-visible, not activatable, and disabled-by-name affects shadowed/effective entries predictably.
- [x] Add `SkillsManagementServiceTests` covering global/project/both bulk enable/disable/invert behavior and no-project project-scope guard.
- [x] Add/update live-tool/runtime tests if existing test seams allow verifying `alta skill list/show/activate` and model advertisements respect disabled config.
- [x] Run `cd src; dotnet test -c Release --filter "SkillCatalog|SkillsManagement|AltaLiveTool|AgentInstruction"` first, then `cd src; dotnet test -c Release` if the targeted set passes.
- [x] Run `cd src; dotnet build -c Release`.
- [x] If docs changed, run `cd site; lunet build` when Lunet is available; otherwise record that the site build could not be run.
- [x] Self-review diff for config serialization churn, unintended changes to unrelated `.alta/config.toml`, and UI behavior when no project is selected.

## Handoff notes
- Do not modify the existing untracked `.alta/config.toml`, `.alta/mcp.json`, or `site/img/alta-plan-mode.png` unless explicitly needed; they appear unrelated to this task.
- Keep the first implementation small: use explicit disabled-name lists and checkbox gates rather than introducing a tri-state inheritance model or future-skill default policy.
- Treat the plan file as versioned with the implementation commit because `.alta/plans/` is not ignored.
