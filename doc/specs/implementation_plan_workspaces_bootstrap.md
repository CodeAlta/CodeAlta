# Implementation Plan: Project Catalog and Bootstrap (Superseded Filename)

This file keeps its historical filename only because older docs link to it.

The workspace-first plan is obsolete.

The current model is:

- a global catalog rooted at `~/.codealta/`
- project descriptors under `projects/<projectSlug>.md`
- a project `slug` used as the normalized reference
- a separate project `name` used for checkout directory naming
- `global` plus `project` scopes only
- machine overrides expressed without a workspace layer

For active guidance, use:

- `doc/specs/implementation_plan.md`
- `doc/specs/filesystem_metadata_catalog_spec.md`
- `doc/specs/codealta_adaptive_orchestration_architecture.md`
